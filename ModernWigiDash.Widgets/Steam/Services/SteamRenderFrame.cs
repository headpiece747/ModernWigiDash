namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The service's render frame (the render tick's face): one call owns the
/// retired-texture drain (the poster cache's and the avatar's — the UAF
/// rule: a texture a background tick retired is freed on the render
/// thread, before the canvas draws) and hands back the poster lookup seam
/// and the live avatar. The render thread no longer spells the paired drain
/// ritual and never sees the concrete cache or the provider's handle.
/// </summary>
internal readonly struct SteamRenderFrame
{
    /// <summary>The poster lookup seam (the service's cache behind the seam).</summary>
    public ISteamPosterLookup Posters { get; }

    /// <summary>The owner's avatar texture (the showcase banner), or null
    /// (the banner draws its placeholder disc). Read on the render thread.</summary>
    public SKBitmap? Avatar { get; }

    internal SteamRenderFrame(ISteamPosterLookup posters, SKBitmap? avatar)
    {
        Posters = posters;
        Avatar = avatar;
    }
}
