using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// Interaction types resolved by a layout strategy.
/// </summary>
internal enum SteamInteractionType
{
    None,
    ActivateGame,
    SelectHero,
    SnapShowcase,
}

/// <summary>
/// The outcome of an interaction handled by a layout strategy.
/// </summary>
internal readonly record struct SteamInteractionResult(
    SteamInteractionType Type,
    SteamGameItem? Game = null,
    uint AppId = 0,
    int SnapDirection = 0)
{
    public static SteamInteractionResult None => default;
    public static SteamInteractionResult Activate(SteamGameItem game) => new(SteamInteractionType.ActivateGame, Game: game, AppId: game.AppId);
    public static SteamInteractionResult Select(uint appId) => new(SteamInteractionType.SelectHero, AppId: appId);
    public static SteamInteractionResult Snap(int direction) => new(SteamInteractionType.SnapShowcase, SnapDirection: direction);
}

/// <summary>
/// A presentation strategy for the Steam Library widget: encapsulates view
/// state (scroll offsets, cursors, selection), layout calculation, hit-testing,
/// and rendering behind a single polymorphic interface.
/// </summary>
internal interface ISteamLayoutStrategy : IDisposable
{
    /// <summary>The presentation mode this strategy satisfies.</summary>
    SteamLayoutMode Mode { get; }

    /// <summary>Renders the layout frame to the canvas.</summary>
    void Render(
        SKCanvas canvas,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SteamRenderFrame frame,
        SteamLibrarySnapshot snapshot,
        SteamFilterMode filterMode,
        SKColor accent,
        SKColor text,
        SKSize fallback,
        SteamLibraryService service);

    /// <summary>Populates the span/list of visible AppIds currently in view.</summary>
    void GetVisibleAppIds(
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback,
        List<uint> destination);

    /// <summary>Resolves a tap gesture at the specified point.</summary>
    SteamInteractionResult HandleTap(
        SKPoint point,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback);

    /// <summary>Applies a drag gesture to the strategy's viewport.</summary>
    void HandleDrag(
        float deltaX,
        float deltaY,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback);

    /// <summary>Resolves the target AppId for a long-press favorite toggle.</summary>
    uint? FindTargetAppId(
        SKPoint point,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback);

    /// <summary>Resets any memoized geometry and view state.</summary>
    void Invalidate();
}
