using System.Text.RegularExpressions;

namespace ModernWigiDash.Sdk;

/// <summary>
/// The log-line rule: the single owner of the shape a log value must have
/// before it reaches a line-oriented log file — embedded newlines flattened
/// to spaces, credential-shaped query params and URL query strings redacted,
/// and the result bounded to <see cref="MaxLineLength"/>. The widget host
/// sink (MainWindow's <c>LogInfo</c>/<c>LogError</c>) and <c>CrashLog</c>
/// route through it, so a multi-line <c>ex.ToString()</c> can no longer
/// corrupt the line-oriented log and the redaction rule has one home. The
/// sanitizer never throws — it runs on error-handling paths where the input
/// may be user-supplied, so a null value reads as the empty string rather
/// than replacing the original failure with a secondary exception.
/// </summary>
public static class LogLine
{
    /// <summary>
    /// The per-value line bound: generous enough to keep an exception's type,
    /// message, and top stack frames, small enough that one value can never
    /// write a multi-megabyte line into the line-oriented log.
    /// </summary>
    public const int MaxLineLength = 2000;

    /// <summary>Redacts credential-shaped query params (case-insensitive
    /// match, fixed lowercase marker) — a log value may echo a failing
    /// request URL that carries a token.</summary>
    private static readonly Regex TokenParamRedactor =
        new(@"(?:access_token|refresh_token|device_code|token)=[^&\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>Strips the query string from embedded URLs — the query is
    /// where tokens ride.</summary>
    private static readonly Regex UrlQueryStripper =
        new(@"(?<url>https?://[^\s""'<>]+)\?[^\s""'<>]*", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Flattens and BOUNDS the value in one span pass (so an oversized value
    /// never allocates the full-size intermediates of chained Replace calls
    /// before being cut down), then redacts: query-strip first, then token
    /// redaction — the redaction marker must not sit inside a URL (its angle
    /// brackets would break the URL pattern).
    /// </summary>
    public static string Sanitize(string? value)
    {
        if (value is null) return string.Empty;

        int limit = Math.Min(value.Length, MaxLineLength);
        Span<char> chars = limit <= 256 ? stackalloc char[limit] : new char[limit];
        int written = 0;
        foreach (char c in value.AsSpan(0, limit))
        {
            chars[written++] = c is '\r' or '\n' ? ' ' : c;
        }
        string flattened = new string(chars[..written]);
        return TokenParamRedactor.Replace(UrlQueryStripper.Replace(flattened, "${url}"), "token=<redacted>");
    }
}
