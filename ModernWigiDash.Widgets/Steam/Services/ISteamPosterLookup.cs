namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The poster-lookup seam the Steam renderers draw through: the published
/// bitmap for an app id, or null (the renderer draws its placeholder). The
/// renderers know this rule, never the concrete LRU cache behind it — the
/// 24-slot bound, the downscaled Rgb565 decode, and the retirement drain
/// stay inside the cache module.
/// </summary>
internal interface ISteamPosterLookup
{
    /// <summary>The render-thread lookup: a hit refreshes recency; a miss
    /// yields null (the placeholder). Never decodes, never blocks.</summary>
    bool TryGet(uint appId, out SKBitmap? bitmap);
}
