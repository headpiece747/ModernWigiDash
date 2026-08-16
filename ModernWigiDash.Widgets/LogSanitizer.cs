namespace ModernWigiDash.Widgets;

/// <summary>
/// Flattens and BOUNDS user-provided strings before interpolation into log
/// lines: embedded newlines cannot inject fake entries, and a multi-megabyte
/// value cannot write a multi-megabyte line. Shared by every module that logs
/// user text (the geocoder's Location queries, the price feeds' symbols), so
/// the rule is pinned and tested once instead of mirrored per module.
/// </summary>
internal static class LogSanitizer
{
    internal const int MaxLogValueLength = 200;

    internal static string Sanitize(string value)
    {
        string flat = value.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= MaxLogValueLength ? flat : flat[..MaxLogValueLength];
    }
}
