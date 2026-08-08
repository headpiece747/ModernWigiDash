using System.IO;

namespace ModernWigiDash.Sdk;

/// <summary>
/// Single shared file-log writer for the display device log
/// (<c>display_device.log</c> next to the executable). All components log
/// through this helper so timestamp format and failure handling cannot drift.
///
/// The underlying stream is opened once and reused (never per call): the frame
/// pipeline logs every send (~30/s per hop), and per-call open/write/close
/// cycles measured ~90 file handles per second across the pipe.
/// </summary>
public static class FileLog
{
    private static readonly Lock Gate = new();
    private static FileStream? _stream;
    private static StreamWriter? _writer;
    private static bool _reportedFailure;

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
                if (_writer == null)
                {
                    _stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    _writer = new StreamWriter(_stream);
                }
                _writer.WriteLine(line);
                _writer.Flush();
            }
            catch (IOException)
            {
                // Log file may be locked or unavailable; logging is best-effort.
                if (!_reportedFailure)
                {
                    _reportedFailure = true;
                    System.Diagnostics.Debug.WriteLine("FileLog: display_device.log write failed (locked or unavailable)");
                }
            }
        }
    }

    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "display_device.log");
}
