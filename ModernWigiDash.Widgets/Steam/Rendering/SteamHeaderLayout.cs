using System.Runtime.InteropServices;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// Touch zone targets in the shared Steam Library header bar.
/// </summary>
public enum SteamHeaderHit
{
    /// <summary>No interactive header element was hit.</summary>
    None,
    /// <summary>The "All" filter pill was tapped.</summary>
    FilterAll,
    /// <summary>The "Favorites" filter pill was tapped.</summary>
    FilterFavorites,
    /// <summary>The "Recent" filter pill was tapped.</summary>
    FilterRecent,
    /// <summary>The layout mode cycle pill was tapped.</summary>
    LayoutToggle,
}

/// <summary>
/// Immutable geometry for the shared header bar across all 3 presentation layouts.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct SteamHeaderGeometry(
    SKRect HeaderBounds,
    SKRect FilterAllBounds,
    SKRect FilterFavoritesBounds,
    SKRect FilterRecentBounds,
    SKRect GameCountBounds,
    SKRect LayoutToggleBounds);

/// <summary>
/// Pure layout calculator and hit testing for the shared Steam Library header.
/// </summary>
public static class SteamHeaderLayout
{
    /// <summary>The design-space width.</summary>
    public const float DesignWidth = DisplayGeometry.FramebufferWidth;

    /// <summary>The design-space height.</summary>
    public const float DesignHeight = DisplayGeometry.FramebufferHeight;

    /// <summary>
    /// Computes the header geometry for the given placement bounds.
    /// </summary>
    /// <param name="bounds">The widget placement bounds.</param>
    /// <returns>Calculated header and pill geometry.</returns>
    public static SteamHeaderGeometry Compute(SKRect bounds)
    {
        float scale = bounds.Height > 0 ? bounds.Height / DesignHeight : 1f;
        float pad = 12f * scale;

        float headerHeight = 38f * scale;
        SKRect headerBounds = new(bounds.Left + pad, bounds.Top + pad, bounds.Right - pad, bounds.Top + pad + headerHeight);

        float pillHeight = 32f * scale;
        float pillY = headerBounds.Top + (headerHeight - pillHeight) / 2f;

        float allWidth = 60f * scale;
        SKRect filterAllBounds = new(headerBounds.Left, pillY, headerBounds.Left + allWidth, pillY + pillHeight);

        float favWidth = 86f * scale;
        SKRect filterFavBounds = new(filterAllBounds.Right + 8f * scale, pillY, filterAllBounds.Right + 8f * scale + favWidth, pillY + pillHeight);

        float recWidth = 76f * scale;
        SKRect filterRecBounds = new(filterFavBounds.Right + 8f * scale, pillY, filterFavBounds.Right + 8f * scale + recWidth, pillY + pillHeight);

        float modeWidth = 96f * scale;
        SKRect layoutToggleBounds = new(headerBounds.Right - modeWidth, pillY, headerBounds.Right, pillY + pillHeight);

        float countWidth = 78f * scale;
        SKRect gameCountBounds = new(layoutToggleBounds.Left - 8f * scale - countWidth, pillY, layoutToggleBounds.Left - 8f * scale, pillY + pillHeight);

        return new SteamHeaderGeometry(
            headerBounds,
            filterAllBounds,
            filterFavBounds,
            filterRecBounds,
            gameCountBounds,
            layoutToggleBounds);
    }

    /// <summary>
    /// Tests which header control contains the given touch point.
    /// </summary>
    /// <param name="header">The computed header geometry.</param>
    /// <param name="point">The touch point to test.</param>
    /// <returns>The hit header element, or None.</returns>
    public static SteamHeaderHit HitTest(SteamHeaderGeometry header, SKPoint point)
    {
        if (header.LayoutToggleBounds.Contains(point))
            return SteamHeaderHit.LayoutToggle;

        if (header.FilterAllBounds.Contains(point))
            return SteamHeaderHit.FilterAll;

        if (header.FilterFavoritesBounds.Contains(point))
            return SteamHeaderHit.FilterFavorites;

        if (header.FilterRecentBounds.Contains(point))
            return SteamHeaderHit.FilterRecent;

        return SteamHeaderHit.None;
    }
}
