using System.IO;
using System.Text.RegularExpressions;

namespace ModernWigiDash.App;

/// <summary>
/// The crash-log writer: appends a sanitized exception line — type name +
/// message — and rotates the file past 5 MB to crash.log.1, mirroring
/// FileLog's rotation. The message is sanitized before it lands: exception
/// text may embed a URL carrying a token, and crash.log is plaintext.
/// Best-effort: a locked file just keeps failing silently (surfaced to debug
/// output). The path is pinned by the App to %LOCALAPPDATA%\ModernWigiDash\
/// (never next to the exe: Program Files is read-only for standard users).
/// </summary>
internal static class CrashLog
{
    /// <summary>The crash log path; pinned by the App at startup (see the
    /// type doc). Tests override it for isolation.</summary>
    internal static string LogPath { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log");

    /// <summary>crash.log rotates to crash.log.1 past this size (FileLog's cap).</summary>
    private const long RotationCapBytes = 5 * 1024 * 1024;

    /// <summary>Redacts <c>token=</c> values (case-insensitive match, fixed
    /// lowercase marker) in a crash message — an exception message may echo a
    /// failing request URL that carries a token.</summary>
    private static readonly Regex TokenParamRedactor =
        new(@"token=[^&\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>Strips the query string from embedded URLs in a crash message.</summary>
    private static readonly Regex UrlQueryStripper =
        new(@"(https?://[^\s""'<>]+)\?[^\s""'<>]*", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public static void Append(Exception? ex, bool handled = false)
    {
        string kind = handled ? "HANDLED EXCEPTION" : "UNHANDLED EXCEPTION";
        string typeName = ex?.GetType().Name ?? "null";
        string message = SanitizeMessage(ex?.Message);
        string msg = $"[{TimeProvider.System.GetUtcNow().UtcDateTime:yyyy-MM-dd HH:mm:ss}] {kind}: {typeName}: {message}{Environment.NewLine}";
        try
        {
            RotateIfNeeded();
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

    /// <summary>
    /// The crash-message sanitizer: an exception message may embed a URL with
    /// a token (an API error echoing the failing request). Strip query strings
    /// from embedded URLs, then redact any remaining <c>token=</c> values,
    /// before the line is appended to crash.log.
    /// </summary>
    internal static string SanitizeMessage(string? message)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        // Query-strip first: the redaction marker must not sit inside a URL
        // (its angle brackets would break the URL pattern).
        return TokenParamRedactor.Replace(UrlQueryStripper.Replace(message, "$1"), "token=<redacted>");
    }

    /// <summary>
    /// Rotates an oversized crash.log to crash.log.1 (replacing an existing
    /// backup) so a fresh file is appended — mirrors FileLog's rotation.
    /// Best-effort: a locked file just keeps appending past the cap.
    /// </summary>
    private static void RotateIfNeeded()
    {
        var current = new FileInfo(LogPath);
        if (!current.Exists || current.Length < RotationCapBytes) return;

        string rotatedPath = LogPath + ".1";
        if (File.Exists(rotatedPath)) File.Delete(rotatedPath);
        File.Move(LogPath, rotatedPath);
    }
}
