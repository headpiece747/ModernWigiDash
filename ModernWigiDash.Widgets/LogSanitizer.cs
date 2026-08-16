namespace ModernWigiDash.Widgets;

/// <summary>
/// Flattens and BOUNDS user-provided strings before interpolation into log
/// lines: embedded newlines cannot inject fake entries, and a multi-megabyte
/// value cannot write a multi-megabyte line. Shared by every module that logs
/// user text (the geocoder's Location queries, the price feeds' symbols), so
/// the rule is pinned and tested once instead of mirrored per module. The
/// sanitizer never throws — it is used inside error-handling paths where the
/// input is user-supplied, so a null value reads as the empty string rather
/// than replacing the original failure with a secondary exception.
/// </summary>
internal static class LogSanitizer
{
    internal const int MaxLogValueLength = 200;

    internal static string Sanitize(string? value)
    {
        if (value is null) return string.Empty;

        // Single pass: flatten and truncate in one span scan so an oversized
        // value never allocates the full-size intermediates of chained
        // Replace calls before being cut down to MaxLogValueLength.
        int limit = Math.Min(value.Length, MaxLogValueLength);
        Span<char> chars = limit <= 256 ? stackalloc char[limit] : new char[limit];
        int written = 0;
        foreach (char c in value.AsSpan(0, limit))
        {
            chars[written++] = c is '\r' or '\n' ? ' ' : c;
        }
        return new string(chars[..written]);
    }
}
