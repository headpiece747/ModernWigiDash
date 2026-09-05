using System.Collections.Concurrent;

namespace ModernWigiDash.Core.Rendering;

/// <summary>
/// The overflow-bound rule for FontHelper's memoization caches: every cache
/// keyed by content variety is bounded by the same coarse reset — when the
/// live entry count exceeds the cache's declared limit, the whole cache is
/// cleared and refills on demand. A whole clear (rather than an LRU partial
/// eviction) is safe because every entry recomputes; an evicted entry costs
/// one recompute. Each cache's cap is declared here — the one place each
/// cache's maximum is spelled.
/// </summary>
public static class FontCacheEviction
{
    /// <summary>The glyph-presence cache cap (typeface handle × codepoint).</summary>
    public const int GlyphPresenceLimit = 4096;

    /// <summary>The codepoint/style → fallback-typeface cache cap.</summary>
    public const int FallbackTypefaceLimit = 2048;

    /// <summary>The text → run-splits cache cap.</summary>
    public const int TextRunsLimit = 2048;

    /// <summary>
    /// The (typeface handle, size) → SKFont cache cap. A dashboard renders a
    /// small fixed set of (family, style, size) pairs (~15-20 across every
    /// built-in widget), so 256 is ample headroom while still letting the
    /// whole-reset eviction actually engage: at 2048 the branch essentially
    /// never fired, leaving the native-font set effectively unbounded for a
    /// long session. An evicted entry costs one recompute (every entry is a
    /// pure recompute), so the lower cap only trims idle native handles.
    /// </summary>
    public const int CachedFontLimit = 256;

    /// <summary>The font-handle → (typeface handle, style) resolution cache cap.</summary>
    public const int FontMetaLimit = 2048;

    /// <summary>
    /// Bounds the cache with the rule: if its live entry count has exceeded
    /// the limit, clear the whole cache (the reset; entries refill on
    /// demand).
    /// </summary>
    public static void EvictIfFull<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache, int limit)
        where TKey : notnull
    {
        if (cache.Count > limit)
        {
            cache.Clear();
        }
    }
}
