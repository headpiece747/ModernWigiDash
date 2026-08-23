using System.IO;

namespace ModernWigiDash.App;

/// <summary>
/// The crash-log writer: appends a sanitized exception line — type name +
/// message — and rotates the file past the shared rotation cap
/// (<see cref="FileLog.RotationCapBytes"/>) to crash.log.1 through the
/// shared rotation primitive (<see cref="FileLog.RotateIfOverCap"/> — the
/// same cap, the same delete-backup-and-move step, one owner). The message
/// is sanitized before it lands through
/// <see cref="ModernWigiDash.Sdk.LogLine"/> (the shared log-line rule):
/// exception text may embed a URL carrying a token, and crash.log is
/// plaintext.
/// Best-effort: a locked file just keeps failing silently (surfaced to debug
/// output). The path is pinned by the App to %LOCALAPPDATA%\ModernWigiDash\
/// (never next to the exe: Program Files is read-only for standard users).
/// </summary>
internal static class CrashLog
{
    /// <summary>The crash log path; pinned by the App at startup (see the
    /// type doc). Tests override it for isolation.</summary>
    internal static string LogPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "crash.log");

    public static void Append(Exception? ex, bool handled = false)
    {
        string kind = handled ? "HANDLED EXCEPTION" : "UNHANDLED EXCEPTION";
        string typeName = ex?.GetType().Name ?? "null";
        string message = LogLine.Sanitize(ex?.Message);
        string msg = $"[{TimeProvider.System.GetUtcNow().UtcDateTime:yyyy-MM-dd HH:mm:ss}] {kind}: {typeName}: {message}{Environment.NewLine}";
        try
        {
            FileLog.RotateIfOverCap(LogPath);
            File.AppendAllText(LogPath, msg);
        }
        catch (IOException)
        {
            // Crash log is best-effort; file may be locked. Surface to debug output.
            System.Diagnostics.Debug.WriteLine("Crash log write failed (file locked)");
        }
        catch (UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("Crash log write failed (access denied)");
        }
    }
}
