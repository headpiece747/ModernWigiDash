using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// Touch zone targets in the Hero Deck layout.
/// </summary>
internal enum SteamHeroHit
{
    /// <summary>The point is on no interactive element.</summary>
    None,

    /// <summary>The "All" category filter pill.</summary>
    FilterAll,

    /// <summary>The "Favorites" category filter pill.</summary>
    FilterFavorites,

    /// <summary>The "Recent" category filter pill.</summary>
    FilterRecent,

    /// <summary>The layout mode switch pill in the top-right.</summary>
    LayoutToggle,

    /// <summary>The primary action CTA button (PLAY NOW / STOP GAME).</summary>
    HeroAction,

    /// <summary>The hero card area.</summary>
    HeroCard,

    /// <summary>A game row on the right-hand shelf.</summary>
    ShelfItem,
}

/// <summary>
/// Geometry for a single game row on the Hero Deck's right-hand shelf.
/// </summary>
internal readonly record struct SteamShelfItemGeometry(
    int Index,
    uint AppId,
    SKRect Bounds,
    SKRect CapsuleThumb,
    SKRect TitleBounds,
    SKRect StatusBounds,
    bool IsSelected);

/// <summary>
/// The immutable geometry for the Hero Deck layout: header controls, hero card
/// with tactile action button, and the vertical shelf. Computed once per layout
/// change and shared between the renderer and touch hit testing.
/// </summary>
internal readonly record struct SteamHeroGeometry(
    SKRect Viewport,
    SKRect HeaderBounds,
    SKRect FilterAllBounds,
    SKRect FilterFavoritesBounds,
    SKRect FilterRecentBounds,
    SKRect GameCountBounds,
    SKRect LayoutToggleBounds,
    SKRect HeroCardBounds,
    SKRect HeroArtBounds,
    SKRect HeroStatusPillBounds,
    SKRect HeroPlaytimePillBounds,
    SKRect HeroTitleBounds,
    SKRect HeroSubtitleBounds,
    SKRect HeroActionButtonBounds,
    SKRect ShelfBounds,
    SteamShelfItemGeometry[] ShelfItems,
    float ScrollY,
    float MaxScrollY);
