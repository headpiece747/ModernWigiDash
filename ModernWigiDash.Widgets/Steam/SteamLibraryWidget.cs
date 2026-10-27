using System.Diagnostics;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam;

/// <summary>
/// The native Steam Library widget: local game discovery (registry +
/// libraryfolders.vdf + ACF), a bounded 24-slot poster texture cache, 3-second
/// process tracking, and steam://run launch / terminate. The widget delegates
/// presentation and touch interaction to polymorphic <see cref="ISteamLayoutStrategy"/>
/// implementations (Hero Deck, Showcase, Poster Grid), retaining only service
/// lifecycle, property configuration, and header command arbitration.
/// </summary>
[WidgetMetadata("steam_library", "Steam Library", Category = "Games", DefaultGridSize = GridSizePreset.Size5x4)]
public sealed class SteamLibraryWidget : ModernWidgetBase, IGestureClaimant
{
    /// <summary>Claims horizontal drag gestures for Poster Grid and Showcase modes so page swipes do not hijack scrolling.</summary>
    public bool ClaimsHorizontalDrag => ActiveMode is SteamLayoutMode.Grid or SteamLayoutMode.Showcase;
    /// <summary>The long-press duration that toggles a favorite (a shorter press is a launch tap).</summary>
    internal static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(600);

    /// <summary>The horizontal or vertical drag distance (design px) that counts as a scroll, not a tap.</summary>
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
    /// the single <c>SetProperty</c> path, never edited in the inspector.
    /// </summary>
    public string FavoritesJson { get; set; } = "[]";

    private readonly SteamNotReadyRenderer _notReadyRenderer = new();
    private readonly SteamGeometryMemo _geometryMemo = new();
    private readonly Dictionary<SteamLayoutMode, ISteamLayoutStrategy> _strategies;
    private readonly ISteamLibrarySource? _testSource;
    private readonly Action<string>? _testLaunchSink;
    private readonly TimeProvider? _testClock;
    private SteamLibraryService? _service;
    private SKRect _lastBounds;
    private SteamFilterMode _activeFilter = SteamFilterMode.All;
    private SKPoint? _touchDownPoint;
    private DateTimeOffset? _touchDownUtc;
    private bool _disposed;

    /// <summary>Production constructor; initializes presentation strategies.</summary>
    public SteamLibraryWidget()
    {
        _strategies = new()
        {
            [SteamLayoutMode.HeroDeck] = new HeroDeckStrategy(_geometryMemo),
            [SteamLayoutMode.Showcase] = new ShowcaseStrategy(_geometryMemo),
            [SteamLayoutMode.Grid] = new GridStrategy(_geometryMemo),
        };
    }

    /// <summary>Test seam: an injectable library source, launch sink, and clock.</summary>
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

    private ISteamLayoutStrategy ActiveStrategy => _strategies[ActiveMode];

    private SteamLayoutMode ActiveMode => LayoutMode switch
    {
        "Showcase" => SteamLayoutMode.Showcase,
        "Poster Grid" => SteamLayoutMode.Grid,
        _ => SteamLayoutMode.HeroDeck,
    };

    /// <summary>
    /// Binds the context, creates the service over the (injected or production)
    /// seams, and starts the 3-second refresh loop.
    /// </summary>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);

        SteamLibraryService? previous = _service;
        _service = null;
        previous?.Dispose();

        _geometryMemo.Invalidate();
        foreach (ISteamLayoutStrategy strategy in _strategies.Values)
            strategy.Invalidate();

        ISteamLibrarySource source = _testSource ?? new SteamLibrarySource(LogDiagnostic);
        Action<string> sink = _testLaunchSink ?? (Action<string>)LaunchViaShell;
        TimeProvider clock = _testClock ?? TimeProvider.System;
        var tracker = new SteamProcessTracker(new SteamProcessBridge(clock), sink);
        var api = new SteamWebApiClient(clock, null);

        _service = new SteamLibraryService(source, tracker, api, OnSnapshotPublished, LogDiagnostic);
        _service.SetFavoritesJson(FavoritesJson);
        _service.SetCredentials(SteamApiKey, SteamId64);

        if (_testSource is null)
            _service.Start();
    }

    private void OnSnapshotPublished(SteamLibrarySnapshot _)
        => Context?.RequestRender();

    /// <summary>
    /// Draws the active presentation layout from the published snapshot.
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

        SteamRenderFrame frame = service.BeginRenderFrame();
        SteamLibrarySnapshot snapshot = service.Current;
        IReadOnlyList<SteamGameItem> games = _geometryMemo.FilteredGames(snapshot, ShowUninstalled, _activeFilter);
        (SKColor accent, SKColor text) = ResolveColors();

        ActiveStrategy.Render(canvas, bounds, games, frame, snapshot, _activeFilter, accent, text, DefaultSize, service);
    }

    /// <summary>
    /// Routes touch interaction: drags scroll the active strategy's view,
    /// long-presses toggle favorites, and taps activate header filters or titles.
    /// </summary>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        SteamLibraryService? service = _service;
        if (service is null)
            return;

        if (eventType == TouchEventType.TouchCancel)
        {
            _touchDownPoint = null;
            _touchDownUtc = null;
            return;
        }

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

        SKRect bounds = _lastBounds.Width > 0f ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
        IReadOnlyList<SteamGameItem> games = _geometryMemo.FilteredGames(service.Current, ShowUninstalled, _activeFilter);

        if (isDrag)
        {
            ActiveStrategy.HandleDrag(dragDeltaX, dragDeltaY, bounds, games, DefaultSize);
            Context?.RequestRender();
            return;
        }

        if (Clock.GetUtcNow() - downUtc >= LongPress)
        {
            ToggleFavoriteAt(localPoint, bounds, games, service);
            return;
        }

        TapAt(localPoint, bounds, games, service);
    }

    /// <summary>A favorites or credentials change is pushed to the service.</summary>
    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        if (string.Equals(propertyName, nameof(FavoritesJson), StringComparison.Ordinal))
            _service?.SetFavoritesJson(FavoritesJson);
        else if (string.Equals(propertyName, nameof(SteamApiKey), StringComparison.Ordinal)
            || string.Equals(propertyName, nameof(SteamId64), StringComparison.Ordinal))
            _service?.SetCredentials(SteamApiKey, SteamId64);

        base.OnPropertyChanged(propertyName, newValue);
    }

    /// <summary>Stops the service, disposes strategies, and releases native handles.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        foreach (ISteamLayoutStrategy strategy in _strategies.Values)
            strategy.Dispose();

        _notReadyRenderer.Dispose();

        SteamLibraryService? service = _service;
        _service = null;
        if (service is not null)
            service.Dispose();

        await base.DisposeAsync().ConfigureAwait(false);
    }

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

    private (SKColor Accent, SKColor Text) ResolveColors()
    {
        SKColor accent = ColorOf("#F59E0B", new SKColor(245, 158, 11));
        SKColor text = ColorOf("#F3F4F6", SKColors.White);
        return (accent, text);
    }

    private void TapAt(SKPoint point, SKRect bounds, IReadOnlyList<SteamGameItem> games, SteamLibraryService service)
    {
        SteamHeaderGeometry header = SteamHeaderLayout.Compute(bounds);
        SteamHeaderHit headerHit = SteamHeaderLayout.HitTest(header, point);

        switch (headerHit)
        {
            case SteamHeaderHit.LayoutToggle:
                CycleLayoutMode();
                return;

            case SteamHeaderHit.FilterAll:
                _activeFilter = SteamFilterMode.All;
                Context?.RequestRender();
                return;

            case SteamHeaderHit.FilterFavorites:
                _activeFilter = SteamFilterMode.Favorites;
                Context?.RequestRender();
                return;

            case SteamHeaderHit.FilterRecent:
                _activeFilter = SteamFilterMode.Recent;
                Context?.RequestRender();
                return;
        }

        SteamInteractionResult result = ActiveStrategy.HandleTap(point, bounds, games, DefaultSize);
        switch (result.Type)
        {
            case SteamInteractionType.ActivateGame:
                if (result.Game is not null)
                    service.Activate(result.Game);
                break;

            case SteamInteractionType.SelectHero:
            case SteamInteractionType.SnapShowcase:
                Context?.RequestRender();
                break;
        }
    }

    private void ToggleFavoriteAt(SKPoint point, SKRect bounds, IReadOnlyList<SteamGameItem> games, SteamLibraryService service)
    {
        uint? appId = ActiveStrategy.FindTargetAppId(point, bounds, games, DefaultSize);
        if (appId is { } id)
        {
            string json = service.ToggleFavorite(id);
            SetProperty(nameof(FavoritesJson), json);
        }
    }

    private void LaunchViaShell(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Context?.LogError($"Steam launch failed for {uri}", ex);
        }
    }
}
