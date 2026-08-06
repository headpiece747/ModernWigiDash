namespace ModernWigiDash.Sdk;

/// <summary>
/// Single shared file-log writer for the display device log
/// (<c>display_device.log</c> next to the executable). All components log
/// through this helper so timestamp format and failure handling cannot drift.
/// </summary>
public static class FileLog
{
    /// <summary>
    /// Writes one line to the shared display log. Never throws: logging is
    /// best-effort and failures (locked/unavailable file) are swallowed.
    /// </summary>
    public static void Write(string message, string? prefix = null)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {(string.IsNullOrEmpty(prefix) ? "" : prefix + " ")}{message}";
            using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.WriteLine(line);
        }
        catch (IOException)
        {
            // Log file may be locked or unavailable; logging is best-effort.
            System.Diagnostics.Debug.WriteLine("FileLog: display_device.log write failed (locked or unavailable)");
        }
    }

    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, "display_device.log");
}
