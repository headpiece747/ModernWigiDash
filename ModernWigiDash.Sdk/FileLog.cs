using System.Diagnostics;
using System.IO;
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
/// The file is rotated when it exceeds 5 MB: the current file is moved to
/// <c>display_device.log.1</c> (an existing backup is replaced) and a fresh
/// file is started. The size check runs on a write cadence (every 100 writes),
/// not per write, so the send path does not stat the file every frame.
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

    private const int FlushThresholdBytes = 8 * 1024;
    private const int RotationCapBytes = 5 * 1024 * 1024;
    private const int RotationCheckIntervalWrites = 100;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Path of the display log. Defaults to <c>display_device.log</c> next to
    /// the executable; overridable so tests can point at a temp path.
    /// </summary>
    public static string LogPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "display_device.log");

    /// <summary>
    /// Writes one line to the shared display log. Never throws: logging is
    /// best-effort and failures (locked/unavailable file) are swallowed.
    /// </summary>
    public static void Write(string message, string? prefix = null)
    {
        string line = $"[{TimeProvider.System.GetUtcNow().UtcDateTime:HH:mm:ss.fff}] {(string.IsNullOrEmpty(prefix) ? "" : prefix + " ")}{message}";
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
                // string length — a line of multi-byte characters otherwise
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

    private static StreamWriter OpenWriter()
    {
        // An oversized file from a previous run is rotated here too (once, at
        // open) so the file is never appended past the cap from a fresh start.
        TryRotateIfNeeded();

        var writer = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        _lastFlushTicks = Stopwatch.GetTimestamp();
        return writer;
    }

    /// <summary>
    /// If the current log file exceeds the rotation cap, closes the writer
    /// (flushing any buffered lines), moves the file to <c>.1</c>, and leaves
    /// the writer closed so the next write opens a fresh file. Best-effort:
    /// a locked file just keeps appending past the cap.
    /// </summary>
    private static void TryRotateIfNeeded()
    {
        var current = new FileInfo(LogPath);
        if (!current.Exists || current.Length < RotationCapBytes) return;

        try
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
            _bufferedBytes = 0;

            string rotatedPath = LogPath + ".1";
            if (File.Exists(rotatedPath))
                File.Delete(rotatedPath);
            File.Move(LogPath, rotatedPath);
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
