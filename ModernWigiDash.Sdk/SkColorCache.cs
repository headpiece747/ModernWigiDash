using System.Collections.Concurrent;

namespace ModernWigiDash.Sdk;

/// <summary>
/// The one owner of hex-to-<c>SKColor</c> parsing in src: a process-wide
/// cache keyed by the hex string, so the per-frame parse sites across the
/// widget layer (the widget base, the canvas draw owners, the seasonal
/// palette, the compositor, the chat status colors) run at most once per
/// distinct value. The cache is bounded by the number of distinct rendered
/// color values the app can have (a handful), not by call sites; a null or
/// blank hex never reaches the parse and returns the fallback, so a blank
/// property value is the fallback color, never a throw. Only successful
/// parses are cached: a failed parse's result is the CALLER'S fallback,
/// which varies per site, so caching it would leak one site's fallback
/// into every other site that passes the same bad value.
/// </summary>
public static class SkColorCache
{
    private static readonly ConcurrentDictionary<string, SKColor> Cache = new(StringComparer.Ordinal);

    /// <summary>Parses the hex, caching successful results per distinct value (a blank or unparseable value is the caller's fallback).</summary>
    public static SKColor ColorOf(string? hex, SKColor fallback)
    {
        if (string.IsNullOrEmpty(hex))
            return fallback;

        if (Cache.TryGetValue(hex, out SKColor cached))
            return cached;

        if (SKColor.TryParse(hex, out SKColor parsed))
        {
            Cache[hex] = parsed;
            return parsed;
        }
        return fallback;
    }
}
