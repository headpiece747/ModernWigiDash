using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ModernWigiDash.Sdk;

/// <summary>
/// Single shared file-log writer for the display device log
/// (<c>display_device.log</c> next to the executable). All components log
/// through this helper so timestamp format and failure handling cannot drift.
///
/// The underlying stream is opened once and reused (never per call): the frame
/// pipeline logs every send (~30/s per hop), and per-call open/write/close
/// cycles measured ~90 file handles per second across the pipe. Flushes are
/// cadence-based (8 KB or 250 ms) instead of per line, so the send path never
/// pays a syscall per frame.
///
/// The file is rotated when it exceeds the shared rotation cap
/// (<see cref="RotationCapBytes"/> â€” the App's crash log rotates at the same
/// bound): the current file is moved to <c>display_device.log.1</c> (an
/// existing backup is replaced) and a fresh file is started. The size check
/// runs on a write cadence (every 100 writes), not per write, so the send
/// path does not stat the file every frame.
/// </summary>
public static class FileLog
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;
    private static int _bufferedBytes;
    private static long _lastFlushTicks;
    private static bool _reportedFailure;
    private static bool _reportedRotationFailure;
    private static int _writesSinceRotationCheck;
    // 1 once this process has emitted its dated banner for the current writer;
    // reset by ResetWriter so the next writer (a rotation, or a path change)
    // gets its own anchor.
    private static int _dateStamped;

    private const int FlushThresholdBytes = 8 * 1024;
    /// <summary>
    /// The file-log rotation cap (5 MB) â€” the one shared rotation bound: the
    /// App's crash log rotates at this same cap (CrashLog).
    /// </summary>
    public const int RotationCapBytes = 5 * 1024 * 1024;
    private const int RotationCheckIntervalWrites = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Path of the display log. Defaults to <c>display_device.log</c> next to
    /// the executable; overridable so tests can point at a temp path. Setting
    /// a new path closes any open writer so subsequent writes open the new
    /// file â€” the writer is process-global and must not outlive a path change
    /// (production sets the path exactly once at startup, before any write).
    /// </summary>
    public static string LogPath
    {
        get => _logPath;
        set
        {
            lock (Gate)
            {
                _logPath = value;
                ResetWriter();
            }
        }
    }

    private static string _logPath = Path.Combine(AppContext.BaseDirectory, "display_device.log");

    /// <summary>Test seam: the clock for the timestamp line (the rest of Sdk
    /// injects TimeProvider â€” this is the same policy for the shared log).</summary>
    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Writes one line to the shared display log. Never throws: logging is
    /// best-effort and failures (locked/unavailable file) are swallowed.
    /// The single owner of the line policy for this file: the message is run
    /// through <see cref="LogLine.Sanitize"/> (flatten + bound + redact) once,
    /// here, so every producer (the DiagLog paths, the raw <c>Log</c>
    /// passthroughs, the direct writers) lands a well-formed line without
    /// spelling the rule itself. Callers bake their component tag into the
    /// message (e.g. "[HW] ...") or route through a bound <see cref="DiagLog"/>.
    /// </summary>
    public static void Write(string message)
    {
        string line = $"[{Stamp()}] {LogLine.Sanitize(message)}";
        lock (Gate)
        {
            try
            {
                if (++_writesSinceRotationCheck >= RotationCheckIntervalWrites)
                {
                    _writesSinceRotationCheck = 0;
                    TryRotateIfNeeded();
                }

                _writer ??= OpenWriter();
                _writer.WriteLine(line);
                // Count the UTF-8 bytes (what actually hits the disk), not the
                // string length â€” a line of multi-byte characters otherwise
                // underestimates the flush threshold.
                _bufferedBytes += Encoding.UTF8.GetByteCount(line) + 2;

                var now = Stopwatch.GetTimestamp();
                if (_bufferedBytes >= FlushThresholdBytes ||
                    Stopwatch.GetElapsedTime(_lastFlushTicks) >= FlushInterval)
                {
                    _writer.Flush();
                    _bufferedBytes = 0;
                    _lastFlushTicks = now;
                }
            }
            catch (IOException)
            {
                // Log file may be locked or unavailable; logging is best-effort.
                ReportFailure();
                ResetWriter();
            }
            catch (UnauthorizedAccessException)
            {
                ReportFailure();
                ResetWriter();
            }
        }
    }

    /// <summary>
    /// Flushes buffered lines to disk immediately (idempotent; a no-op when no
    /// writer is open). The app calls this exactly once at exit: the cadence
    /// flushes (8 KB or 250 ms) may leave the final lines â€” including the
    /// shutdown standby verdict â€” in the buffer, and a process exit does not
    /// reliably run the writer's finalizer (observed on-device: one close lost
    /// the standby line, the next kept it). Never throws.
    /// </summary>
    public static void Flush()
    {
        lock (Gate)
        {
            try
            {
                _writer?.Flush();
                _bufferedBytes = 0;
                _lastFlushTicks = Stopwatch.GetTimestamp();
            }
            catch (IOException)
            {
                ReportFailure();
                ResetWriter();
            }
        }
    }

    private static StreamWriter OpenWriter()
    {
        // An oversized file from a previous run is rotated here too (once, at
        // open) so the file is never appended past the cap from a fresh start.
        TryRotateIfNeeded();

        var writer = new StreamWriter(new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        _lastFlushTicks = Stopwatch.GetTimestamp();
        return writer;
    }

    /// <summary>
    /// The line stamp: UTC time-of-day for an ordinary line, and the full UTC
    /// DATE for the first line of each session (a fresh process, or a file that
    /// was just rotated into).
    ///
    /// The date exists because time-of-day alone leaves a multi-day log
    /// impossible to place: a log spanning a week cannot be lined up against a
    /// Windows event-log entry, and reconstructing the order of a session by
    /// hand is exactly the cost this removes (the 2026-09-27 investigation, where
    /// a multi-day incident could not be correlated with the Windows update
    /// behind it). Only ONE line per session pays for it, so the per-frame paths
    /// keep the cheap stamp â€” and no extra line is added, so line counts and
    /// line-oriented readers are unaffected.
    /// </summary>
    private static string Stamp()
    {
        DateTime now = Clock.GetUtcNow().UtcDateTime;
        return Interlocked.Exchange(ref _dateStamped, 1) == 0
            ? now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
            : now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The shared rotation step: when the file at <paramref name="path"/>
    /// exists and exceeds <see cref="RotationCapBytes"/>, it is moved to
    /// <c>path + ".1"</c>, replacing an existing backup. Returns true when it
    /// rotated, false when the file was absent or under the cap. One rule for
    /// the shared bound â€” the display log and the App's crash log both
    /// rotate through it. A locked file throws <see cref="IOException"/> (or
    /// <see cref="UnauthorizedAccessException"/>) â€” the caller owns the
    /// best-effort policy (this class swallows and reports once; the crash
    /// log lets its append path swallow).
    /// </summary>
    public static bool RotateIfOverCap(string path)
    {
        var current = new FileInfo(path);
        if (!current.Exists || current.Length < RotationCapBytes) return false;

        string rotatedPath = path + ".1";
        if (File.Exists(rotatedPath))
            File.Delete(rotatedPath);
        File.Move(path, rotatedPath);
        return true;
    }

    /// <summary>
    /// If the current log file exceeds the rotation cap, closes the writer
    /// (flushing any buffered lines), moves the file to <c>.1</c>, and leaves
    /// the writer closed so the next write opens a fresh file. Best-effort:
    /// a locked file just keeps appending past the cap.
    /// </summary>
    private static void TryRotateIfNeeded()
    {
        var current = new FileInfo(_logPath);
        if (!current.Exists || current.Length < RotationCapBytes) return;

        try
        {
            // The writer closes before the move â€” a file with an open handle
            // cannot be moved on Windows. If the move fails, the next write
            // reopens the same file and keeps appending.
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
            _bufferedBytes = 0;
            // A fresh file is a new session: its first line carries the date.
            Interlocked.Exchange(ref _dateStamped, 0);

            RotateIfOverCap(_logPath);
        }
        catch (IOException)
        {
            ReportRotationFailure();
        }
        catch (UnauthorizedAccessException)
        {
            ReportRotationFailure();
        }
    }

    /// <summary>Drops the stream so the next write reopens (file deleted mid-run etc.).</summary>
    private static void ResetWriter()
    {
        try { _writer?.Dispose(); } catch { /* best-effort */ }
        _writer = null;
        _bufferedBytes = 0;
        // A reopened writer is a new session: its first line carries the date.
        Interlocked.Exchange(ref _dateStamped, 0);
    }

    private static void ReportFailure()
    {
        if (_reportedFailure) return;
        _reportedFailure = true;
        System.Diagnostics.Debug.WriteLine("FileLog: display_device.log write failed (locked or unavailable)");
    }

    private static void ReportRotationFailure()
    {
        if (_reportedRotationFailure) return;
        _reportedRotationFailure = true;
        System.Diagnostics.Debug.WriteLine("FileLog: display_device.log rotation failed (file locked or unavailable)");
    }
}
