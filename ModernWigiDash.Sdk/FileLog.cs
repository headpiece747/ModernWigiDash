using System.Diagnostics;
using System.IO;

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
/// </summary>
public static class FileLog
{
    private static readonly Lock Gate = new();
    private static FileStream? _stream;
    private static StreamWriter? _writer;
    private static int _bufferedBytes;
    private static long _lastFlushTicks;
    private static bool _reportedFailure;

    private const int FlushThresholdBytes = 8 * 1024;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

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
                _writer ??= OpenWriter();
                _writer.WriteLine(line);
                _bufferedBytes += line.Length + 2;

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
        _stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(_stream);
        _lastFlushTicks = Stopwatch.GetTimestamp();
        return writer;
    }

    /// <summary>Drops the stream so the next write reopens (file deleted mid-run etc.).</summary>
    private static void ResetWriter()
    {
        try { _writer?.Dispose(); } catch { /* best-effort */ }
        _writer = null;
        _stream = null;
        _bufferedBytes = 0;
    }

    private static void ReportFailure()
    {
        if (_reportedFailure) return;
        _reportedFailure = true;
        System.Diagnostics.Debug.WriteLine("FileLog: display_device.log write failed (locked or unavailable)");
    }

    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "display_device.log");
}
