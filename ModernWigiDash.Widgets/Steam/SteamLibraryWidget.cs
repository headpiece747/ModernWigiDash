using System.Diagnostics;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam;

/// <summary>
/// The native Steam Library widget: local game discovery (registry +
/// libraryfolders.vdf + ACF), a bounded 24-slot poster texture cache, 3-second
/// process tracking, and steam://run launch / terminate. The widget keeps only
/// orchestration: the service lifecycle, the property surface, the touch
/// routing (tap launches an idle title and terminates a running one, a 600 ms
/// long-press favorites, a drag scrolls), and the render dispatch to the
/// grid/showcase renderers. The render tick reads the published snapshot and
/// the poster cache only — no disk, no network, no per-tick allocation beyond
/// the cached-font fast path.
/// </summary>
[WidgetMetadata("steam_library", "Steam Library", Category = "Games", DefaultGridSize = GridSizePreset.Size5x4)]
public sealed class SteamLibraryWidget : ModernWidgetBase
{
    /// <summary>The long-press duration that toggles a favorite (a shorter press is a launch tap).</summary>
    internal static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(600);

    /// <summary>The horizontal drag distance (design px) that counts as a scroll, not a tap.</summary>
    internal const float DragThreshold = 24f;

    /// <summary>The "Display Layout" property: Hero Deck, Showcase, or Poster Grid.</summary>
    [WidgetProperty("Display Layout", WidgetPropertyType.Choice, "Presentation layout: Hero Deck (tactile split), Showcase, or Poster Grid.", "Hero Deck", "Hero Deck", "Showcase", "Poster Grid")]
    public string LayoutMode { get; set; } = "Hero Deck";

    /// <summary>The "Show Uninstalled" property: include not-installed titles in the library.</summary>
    [WidgetProperty("Show Uninstalled", WidgetPropertyType.Boolean, "Include titles that are not installed on any library volume.", false)]
    public bool ShowUninstalled { get; set; }

    /// <summary>The "Steam API Key" property: enables the optional community-API persona (dormant when empty).</summary>
    [WidgetProperty("Steam API Key", WidgetPropertyType.Text, "Optional: a Steam Web API key for the owner persona. Empty leaves the API dormant.", "")]
    public string SteamApiKey { get; set; } = "";

    /// <summary>The "Steam ID 64" property: the 17-digit SteamID64 the persona is fetched for.</summary>
    [WidgetProperty("Steam ID 64", WidgetPropertyType.Text, "Optional: the 17-digit SteamID64 for the owner persona. Empty leaves the API dormant.", "")]
    public string SteamId64 { get; set; } = "";

    /// <summary>
    /// The persisted favorites (a uint app-id JSON array). Deliberately NOT a
    /// <c>[WidgetProperty]</c> — it is written by the long-press toggle through
    /// the single <c>SetProperty</c> path, never edited in the inspector. The
    /// service owns the set and hands back the canonical JSON; this property
    /// is its persisted fact, not a second owner.
    /// </summary>
    public string FavoritesJson { get; set; } = "[]";

    private readonly SteamHeroRenderer _heroRenderer = new();
    private readonly SteamGridRenderer _gridRenderer = new();
    private readonly SteamShowcaseRenderer _showcaseRenderer = new();
    private readonly SteamNotReadyRenderer _notReadyRenderer = new();
    private readonly SteamGeometryMemo _geometryMemo = new();
    private readonly ISteamLibrarySource? _testSource;
    private readonly Action<string>? _testLaunchSink;
    private readonly TimeProvider? _testClock;
    private SteamLibraryService? _service;
    private SKRect _lastBounds;
    private float _scrollX;
    private float _heroShelfScrollY;
    private uint _selectedHeroAppId;
    private SteamFilterMode _activeFilter = SteamFilterMode.All;
    private SKPoint? _touchDownPoint;
    private DateTimeOffset? _touchDownUtc;
    private bool _disposed;

    /// <summary>Production constructor; the service is bound in InitializeAsync over the context.</summary>
    public SteamLibraryWidget()
    {
    }

    /// <summary>Test seam: an injectable library source, launch sink, and clock (the 3s loop is not started; tests drive RefreshNow).</summary>
    internal SteamLibraryWidget(ISteamLibrarySource source, Action<string> launchSink, TimeProvider clock)
        : this()
    {
        _testSource = source;
        _testLaunchSink = launchSink;
        _testClock = clock;
    }

    /// <summary>Test seam: the bound service (tests drive RefreshNow and inspect the cache).</summary>
    internal SteamLibraryService? ServiceForTest => _service;

    /// <summary>The test clock (the long-press timing), or the system clock in production.</summary>
    private TimeProvider Clock => _testClock ?? TimeProvider.System;

    /// <summary>
    /// Binds the context, creates the service over the (injected or production)
    /// seams, and starts the 3-second refresh loop (production only — the test
    /// seam drives RefreshNow so a test never waits on the loop).
    /// </summary>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);

        // A rehydrated instance re-runs this method: release the previous
        // service (its loop, poster policy, and avatar) instead of orphaning
        // it.
        SteamLibraryService? previous = _service;
        _service = null;
        previous?.Dispose();
        _geometryMemo.Invalidate();

        ISteamLibrarySource source = _testSource ?? new SteamLibrarySource(msg => Context?.LogError(msg));
        Action<string> sink = _testLaunchSink ?? (Action<string>)LaunchViaShell;
        TimeProvider clock = _testClock ?? TimeProvider.System;
        var tracker = new SteamProcessTracker(new SteamProcessBridge(clock), sink);
        var api = new SteamWebApiClient(clock, null);
        // The service is the composition root: it builds the poster policy
        // itself, so the widget drives the render facade, never the policy handle.
        _service = new SteamLibraryService(source, tracker, api, OnSnapshotPublished, msg => Context?.LogError(msg));
        _service.SetFavoritesJson(FavoritesJson);
        _service.SetCredentials(SteamApiKey, SteamId64);

        if (_testSource is null)
            _service.Start();
    }

    // The publish sink is the render wake-up only: the service owns the
    // snapshot fact (the render tick reads service.Current), so the sink
    // carries no copy — a second owner of the fact is unrepresentable.
    private void OnSnapshotPublished(SteamLibrarySnapshot _)
        => Context?.RequestRender();

    /// <summary>
    /// Draws the grid or the showcase from the published snapshot. The
    /// service's render frame owns the retired-texture drain (the UAF rule:
    /// a texture a background tick retired is freed on the render thread,
    /// before the canvas draws) and hands back the poster lookup + avatar.
    /// </summary>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _lastBounds = bounds;
        SteamLibraryService? service = _service;
        if (service is null)
        {
            _notReadyRenderer.Draw(canvas, bounds);
            return;
        }

        // The render frame owns the retired-texture drain (the UAF rule: a
        // texture a background tick retired is freed here, before the canvas
        // draws) and hands back the poster lookup seam + the live avatar.
        SteamRenderFrame frame = service.BeginRenderFrame();
        // The snapshot fact has one owner: the service's published reference
        // (the render tick reads it; the render-view memo keys its filtered
        // list on the reference, so a fresh tick is a fresh content change).
        SteamLibrarySnapshot snapshot = service.Current;
        IReadOnlyList<SteamGameItem> games = _geometryMemo.FilteredGames(snapshot, ShowUninstalled, _activeFilter);
        (SKColor accent, SKColor text) = ResolveColors();

        switch (ActiveMode)
        {
            case SteamLayoutMode.HeroDeck:
                SteamGameItem? hero = null;
                if (games.Count > 0)
                {
                    hero = games.FirstOrDefault(g => g.AppId == _selectedHeroAppId) ?? games[0];
                    if (_selectedHeroAppId != hero.AppId)
                        _selectedHeroAppId = hero.AppId;
                }
                SteamHeroGeometry heroGeom = _geometryMemo.Hero(bounds, games, _selectedHeroAppId, _heroShelfScrollY, DefaultSize);
                service.RequestHeroPosters(hero, games, heroGeom);
                _heroRenderer.Render(canvas, bounds, heroGeom, hero, games, frame.Posters, _activeFilter, accent, text);
                break;

            case SteamLayoutMode.Showcase:
                (SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next) = _geometryMemo.Selection(games);
                service.RequestShowcasePosters(focus, previous, next);
                _showcaseRenderer.Render(canvas, bounds, ShowcaseGeometry(previous is not null, next is not null), focus, previous, next, frame.Posters, snapshot.Persona, frame.Avatar, accent, text);
                break;

            default:
                SteamGridGeometry geometry = GridGeometry(bounds, games.Count);
                service.RequestVisibleGridPosters(games, geometry);
                _gridRenderer.Render(canvas, bounds, games, geometry, frame.Posters, accent, text);
                break;
        }
    }

    /// <summary>The grid geometry, recomputed only when the bounds, the scroll
    /// offset, or the game count changes (a steady-state frame reuses it, so
    /// the render tick allocates no cell arrays). The memo owns the
    /// recompute-on-change decision; the widget holds only the record.</summary>
    private SteamGridGeometry GridGeometry(SKRect bounds, int gameCount)
        => _geometryMemo.Grid(bounds, _scrollX, gameCount, DefaultSize);

    /// <summary>The showcase geometry, recomputed only when the bounds or the
    /// flank visibility changes (the grid memo's shape, for the showcase
    /// record): render and touch read the same cached record, so the drawn
    /// cards and the tap targets cannot drift apart.</summary>
    private SteamShowcaseGeometry ShowcaseGeometry(bool hasPrevious, bool hasNext)
        => _geometryMemo.Showcase(_lastBounds, hasPrevious, hasNext, DefaultSize);

    /// <summary>
    /// The one touch hit entry: which game a point hits (the grid's cell
    /// index) and which showcase zone (the flanking-card snap targets and the
    /// focal card). The geometry is the cached record, so the hit test reads
    /// exactly what the render tick drew. The unused branch of the tuple
    /// reads as a miss.
    /// </summary>
    private (int GridIndex, SteamShowcaseHit Showcase) HitAt(SKPoint point, IReadOnlyList<SteamGameItem> games)
    {
        if (ActiveMode == SteamLayoutMode.Showcase)
        {
            (SteamGameItem? _, SteamGameItem? previous, SteamGameItem? next) = _geometryMemo.Selection(games);
            return (-1, SteamShowcaseLayout.HitTest(ShowcaseGeometry(previous is not null, next is not null), point));
        }

        SKRect bounds = _lastBounds.Width > 0f ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
        SteamGridGeometry grid = GridGeometry(bounds, games.Count);
        return (SteamGridLayout.HitTest(grid, point), SteamShowcaseHit.None);
    }

    /// <summary>
    /// Routes a touch: a horizontal or vertical drag scrolls the view,
    /// a 600 ms long-press toggles the favorite under the finger,
    /// and a short tap activates action buttons, mode switches, or launches games.
    /// </summary>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        SteamLibraryService? service = _service;
        if (service is null)
            return;

        if (eventType == TouchEventType.TouchDown)
        {
            _touchDownPoint = localPoint;
            _touchDownUtc = Clock.GetUtcNow();
            return;
        }

        if (eventType != TouchEventType.TouchUp || _touchDownPoint is not { } down || _touchDownUtc is not { } downUtc)
            return;

        _touchDownPoint = null;
        _touchDownUtc = null;
        float dragDeltaX = localPoint.X - down.X;
        float dragDeltaY = localPoint.Y - down.Y;
        bool isDrag = Math.Abs(dragDeltaX) > DragThreshold || Math.Abs(dragDeltaY) > DragThreshold;

        if (isDrag)
        {
            switch (ActiveMode)
            {
                case SteamLayoutMode.HeroDeck:
                    SKRect heroBounds = _lastBounds.Width > 0f ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
                    IReadOnlyList<SteamGameItem> currentGames = _geometryMemo.FilteredGames(service.Current, ShowUninstalled, _activeFilter);
                    SteamHeroGeometry hGeom = _geometryMemo.Hero(heroBounds, currentGames, _selectedHeroAppId, _heroShelfScrollY, DefaultSize);
                    _heroShelfScrollY = Math.Clamp(_heroShelfScrollY - dragDeltaY, 0f, hGeom.MaxScrollY);
                    Context?.RequestRender();
                    break;

                case SteamLayoutMode.Showcase:
                    SnapShowcase(service, dragDeltaX > 0f ? -1 : 1);
                    break;

                case SteamLayoutMode.Grid:
                    ScrollGrid(service, dragDeltaX);
                    break;
            }
            return;
        }

        if (Clock.GetUtcNow() - downUtc >= LongPress)
        {
            ToggleFavoriteAt(localPoint, service);
            return;
        }

        TapAt(localPoint, service);
    }

    /// <summary>A favorites change is pushed to the service (the fold reads it) and persisted through the one write path.</summary>
    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        if (string.Equals(propertyName, nameof(FavoritesJson), StringComparison.Ordinal))
            _service?.SetFavoritesJson(FavoritesJson);
        else if (string.Equals(propertyName, nameof(SteamApiKey), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(SteamId64), StringComparison.Ordinal))
            _service?.SetCredentials(SteamApiKey, SteamId64);

        base.OnPropertyChanged(propertyName, newValue);
    }

    /// <summary>Stops the service, disposes the renderers, and releases the native handles.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _heroRenderer.Dispose();
        _gridRenderer.Dispose();
        _showcaseRenderer.Dispose();
        _notReadyRenderer.Dispose();

        SteamLibraryService? service = _service;
        _service = null;
        if (service is not null)
            service.Dispose();

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private SteamLayoutMode ActiveMode => LayoutMode switch
    {
        "Showcase" => SteamLayoutMode.Showcase,
        "Poster Grid" => SteamLayoutMode.Grid,
        _ => SteamLayoutMode.HeroDeck,
    };

    private void CycleLayoutMode()
    {
        string nextMode = LayoutMode switch
        {
            "Hero Deck" => "Showcase",
            "Showcase" => "Poster Grid",
            _ => "Hero Deck",
        };
        LayoutMode = nextMode;
        SetProperty(nameof(LayoutMode), nextMode);
        Context?.RequestRender();
    }

    /// <summary>
    /// The accent and text colors for the render, resolved through the cached
    /// <c>ColorOf</c> fast path (theme-independent defaults; the render tick
    /// never re-parses a hex string).
    /// </summary>
    private (SKColor Accent, SKColor Text) ResolveColors()
    {
        SKColor accent = ColorOf("#F59E0B", new SKColor(245, 158, 11));
        SKColor text = ColorOf("#F3F4F6", SKColors.White);
        return (accent, text);
    }

    private void SnapShowcase(SteamLibraryService service, int direction)
    {
        IReadOnlyList<SteamGameItem> games = _geometryMemo.FilteredGames(service.Current, ShowUninstalled, _activeFilter);
        _geometryMemo.Snap(games, direction);
        Context?.RequestRender();
    }

    private void ScrollGrid(SteamLibraryService service, float dragDelta)
    {
        SKRect bounds = _lastBounds.Width > 0f ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
        SteamGridGeometry geometry = GridGeometry(bounds, _geometryMemo.FilteredGames(service.Current, ShowUninstalled, _activeFilter).Count);
        _scrollX = Math.Clamp(_scrollX - dragDelta, 0f, geometry.MaxScroll);
        Context?.RequestRender();
    }

    private void TapAt(SKPoint point, SteamLibraryService service)
    {
        SKRect bounds = _lastBounds.Width > 0f ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
        float scale = bounds.Height > 0 ? bounds.Height / SteamHeroLayout.DesignHeight : 1f;
        float pad = 12f * scale;
        float pillY = bounds.Top + pad + (38f * scale - 32f * scale) / 2f;
        SKRect layoutToggleBounds = new(bounds.Right - pad - 100f * scale, pillY, bounds.Right - pad, pillY + 32f * scale);

        if (layoutToggleBounds.Contains(point))
        {
            CycleLayoutMode();
            return;
        }

        IReadOnlyList<SteamGameItem> games = _geometryMemo.FilteredGames(service.Current, ShowUninstalled, _activeFilter);

        if (ActiveMode == SteamLayoutMode.HeroDeck)
        {
            SteamHeroGeometry geom = _geometryMemo.Hero(bounds, games, _selectedHeroAppId, _heroShelfScrollY, DefaultSize);
            var (heroHit, shelfIndex) = SteamHeroLayout.HitTest(geom, point);

            switch (heroHit)
            {
                case SteamHeroHit.LayoutToggle:
                    CycleLayoutMode();
                    return;

                case SteamHeroHit.FilterAll:
                    _activeFilter = SteamFilterMode.All;
                    Context?.RequestRender();
                    return;

                case SteamHeroHit.FilterFavorites:
                    _activeFilter = SteamFilterMode.Favorites;
                    Context?.RequestRender();
                    return;

                case SteamHeroHit.FilterRecent:
                    _activeFilter = SteamFilterMode.Recent;
                    Context?.RequestRender();
                    return;

                case SteamHeroHit.HeroAction:
                    if (games.Count > 0)
                    {
                        SteamGameItem? hero = games.FirstOrDefault(g => g.AppId == _selectedHeroAppId) ?? games[0];
                        if (hero is not null)
                            service.Activate(hero);
                    }
                    return;

                case SteamHeroHit.ShelfItem:
                    if (shelfIndex >= 0 && shelfIndex < games.Count)
                    {
                        _selectedHeroAppId = games[shelfIndex].AppId;
                        Context?.RequestRender();
                    }
                    return;

                case SteamHeroHit.HeroCard:
                default:
                    return;
            }
        }

        if (games.Count == 0)
            return;

        (int index, SteamShowcaseHit showcaseHit) = HitAt(point, games);

        if (ActiveMode == SteamLayoutMode.Showcase)
        {
            (SteamGameItem? focus, _, _) = _geometryMemo.Selection(games);
            switch (showcaseHit)
            {
                case SteamShowcaseHit.Previous:
                    SnapShowcase(service, -1);
                    break;
                case SteamShowcaseHit.Next:
                    SnapShowcase(service, 1);
                    break;
                case SteamShowcaseHit.Focus:
                    if (focus is not null)
                        service.Activate(focus);
                    break;
            }
            return;
        }

        if (index >= 0 && index < games.Count)
            service.Activate(games[index]);
    }

    private void ToggleFavoriteAt(SKPoint point, SteamLibraryService service)
    {
        IReadOnlyList<SteamGameItem> games = _geometryMemo.FilteredGames(service.Current, ShowUninstalled, _activeFilter);
        if (games.Count == 0)
            return;

        uint? appId = null;

        if (ActiveMode == SteamLayoutMode.HeroDeck)
        {
            SKRect bounds = _lastBounds.Width > 0f ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
            SteamHeroGeometry geom = _geometryMemo.Hero(bounds, games, _selectedHeroAppId, _heroShelfScrollY, DefaultSize);
            var (heroHit, shelfIndex) = SteamHeroLayout.HitTest(geom, point);
            if (heroHit == SteamHeroHit.ShelfItem && shelfIndex >= 0 && shelfIndex < games.Count)
            {
                appId = games[shelfIndex].AppId;
            }
            else if (heroHit is SteamHeroHit.HeroCard or SteamHeroHit.HeroAction)
            {
                SteamGameItem? hero = games.FirstOrDefault(g => g.AppId == _selectedHeroAppId) ?? games[0];
                appId = hero.AppId;
            }
        }
        else if (ActiveMode == SteamLayoutMode.Showcase)
        {
            (SteamGameItem? focus, _, _) = _geometryMemo.Selection(games);
            (int _, SteamShowcaseHit showcaseHit) = HitAt(point, games);
            if (showcaseHit == SteamShowcaseHit.Focus && focus is not null)
                appId = focus.AppId;
        }
        else
        {
            (int index, _) = HitAt(point, games);
            if (index >= 0 && index < games.Count)
                appId = games[index].AppId;
        }

        if (appId is not { } id)
            return;

        string json = service.ToggleFavorite(id);
        SetProperty(nameof(FavoritesJson), json);
    }

    private void LaunchViaShell(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // A failed shell launch (Steam not registered as a steam://
            // handler) is logged once, never thrown into the touch path.
            Context?.LogError($"Steam launch failed for {uri}", ex);
        }
    }
}
