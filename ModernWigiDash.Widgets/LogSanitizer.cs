namespace ModernWigiDash.Widgets;

/// <summary>
/// The per-user-value log cap: flattens and bounds user-provided strings to
/// <see cref="MaxLogValueLength"/> before interpolation into log lines —
/// embedded newlines cannot inject fake entries, and a multi-megabyte value
/// cannot write a multi-megabyte line. Shared by every module that logs
/// user text (the geocoder's Location queries, the price feeds' symbols), so
/// the cap is pinned and tested once instead of mirrored per module. The
/// flatten-and-bound pass itself lives in Sdk's
/// <see cref="ModernWigiDash.Sdk.LogLine.Flatten"/> — this module is the
/// named cap over it (200 per value vs. LogLine's 2000 per line), so the two
/// caps and the one flatten rule can never drift. Never throws: a null value
/// reads as the empty string rather than replacing the original failure with
/// a secondary exception.
/// </summary>
internal static class LogSanitizer
{
    internal const int MaxLogValueLength = 200;

    internal static string Sanitize(string? value) => LogLine.Flatten(value, MaxLogValueLength);
}
