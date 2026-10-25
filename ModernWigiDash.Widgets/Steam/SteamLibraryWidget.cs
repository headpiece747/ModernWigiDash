using System.Diagnostics;
using System.Text.Json;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam;

/// <summary>
/// The native Steam Library widget: local game discovery (registry +
/// libraryfolders.vdf + ACF), a bounded 24-slot poster texture cache, 3-second
/// process tracking, and steam://run launch/terminate. The widget keeps only
/// orchestration: the service lifecycle, the property surface, the touch
/// routing (tap launches, a 600 ms long-press favorites, a drag scrolls),
/// and the render dispatch to the grid/showcase renderers. The render tick
/// reads the published snapshot and the poster cache only — no disk, no
/// network, no per-tick allocation beyond the cached-font fast path.
/// </summary>
[WidgetMetadata("steam_library", "Steam Library", Category = "Games", DefaultGridSize = GridSizePreset.Size5x4)]
public sealed class SteamLibraryWidget : ModernWidgetBase
{
    /// <summary>The long-press duration that toggles a favorite (a shorter press is a launch tap).</summary>
    internal static readonly TimeSpan LongPress = TimeSpan.FromMilliseconds(600);

    /// <summary>The horizontal drag distance (design px) that counts as a scroll, not a tap.</summary>
    internal const float DragThreshold = 24f;

    /// <summary>The "Display Layout" property: the poster grid or the showcase.</summary>
    [WidgetProperty("Display Layout", WidgetPropertyType.Choice, "Poster grid (drag to scroll) or showcase (swipe to snap).", "Poster Grid", "Poster Grid", "Showcase")]
    public string LayoutMode { get; set; } = "Poster Grid";

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

    private readonly SteamGridRenderer _gridRenderer = new();
    private readonly SteamShowcaseRenderer _showcaseRenderer = new();
    private readonly ISteamLibrarySource? _testSource;
    private readonly Action<string>? _testLaunchSink;
    private readonly TimeProvider? _testClock;
    private SteamLibraryService? _service;
    private volatile SteamLibrarySnapshot _snapshot = SteamLibrarySnapshot.Empty;
    private SKRect _lastBounds;
    private float _scrollX;
    private int _showcaseIndex;
    private SKPoint? _touchDownPoint;
    private DateTimeOffset? _touchDownUtc;
    private bool _disposed;

    /// <summary>Production constructor; the service is bound in InitializeAsync over the context.</summary>
    public SteamLibraryWidget()
    {
    }

    /// <summary>Test seam: an injectable library source, launch sink, and clock (the 3s loop is not started; tests drive RefreshNow).</summary>
    internal SteamLibraryWidget(ISteamLibrarySource source, Action<string> launchSink, TimeProvider clock)
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

        ISteamLibrarySource source = _testSource ?? new SteamLibrarySource(msg => Context?.LogError(msg));
        Action<string> sink = _testLaunchSink ?? (Action<string>)LaunchViaShell;
        TimeProvider clock = _testClock ?? TimeProvider.System;
        var tracker = new SteamProcessTracker(new SteamProcessBridge(clock), sink);
        var cache = new SteamPosterCache();
        var api = new SteamWebApiClient(clock, null);
        _service = new SteamLibraryService(source, tracker, cache, api, OnSnapshotPublished, msg => Context?.LogError(msg));
        _service.SetFavoritesJson(FavoritesJson);

        if (_testSource is null)
            _service.Start();
    }

    private void OnSnapshotPublished(SteamLibrarySnapshot snapshot)
    {
        _snapshot = snapshot;
        Context?.RequestRender();
    }

    /// <summary>
    /// Draws the grid or the showcase from the published snapshot. The
    /// poster-cache drain runs on the render thread (the UAF rule): a texture
    /// a background tick retired is freed here, before the canvas draws.
    /// </summary>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _lastBounds = bounds;
        SteamLibraryService? service = _service;
        if (service is null)
        {
            DrawNotReady(canvas, bounds);
            return;
        }

        service.PosterCache.DisposeRetired();
        IReadOnlyList<SteamGameItem> games = FilterGames(_snapshot.Games);
        (SKColor accent, SKColor text) = ResolveColors();

        if (IsShowcase)
        {
            (SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next) = ShowcaseSelection(games);
            _showcaseRenderer.Render(canvas, bounds, focus, previous, next, service.PosterCache, accent, text);
        }
        else
        {
            _gridRenderer.Render(canvas, bounds, games, service.PosterCache, _scrollX, accent, text);
        }
    }

    /// <summary>
    /// Routes a touch: a horizontal drag scrolls the grid (or snaps the
    /// showcase), a 600 ms long-press toggles the favorite under the finger,
    /// and a short tap launches the hit title (grid cell or the showcase
    /// focal card).
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
        float dragDelta = localPoint.X - down.X;
        bool isDrag = Math.Abs(dragDelta) > DragThreshold;

        if (isDrag)
        {
            if (IsShowcase)
                SnapShowcase(dragDelta > 0f ? -1 : 1);
            else
                ScrollGrid(dragDelta);
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

        base.OnPropertyChanged(propertyName, newValue);
    }

    /// <summary>Stops the service, disposes the renderers, and releases the native handles.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        _gridRenderer.Dispose();
        _showcaseRenderer.Dispose();

        SteamLibraryService? service = _service;
        _service = null;
        if (service is not null)
            service.Dispose();

        await base.DisposeAsync().ConfigureAwait(false);
    }

    private bool IsShowcase => string.Equals(LayoutMode, "Showcase", StringComparison.Ordinal);

    /// <summary>
    /// The accent and text colors for the render, resolved through the cached
    /// <c>ColorOf</c> fast path (theme-independent defaults; the render tick
    /// never re-parses a hex string).
    /// </summary>
    private (SKColor Accent, SKColor Text) ResolveColors()
    {
        SKColor accent = ColorOf("#66C0F4", SKColors.White);
        SKColor text = ColorOf("#E6E8EA", SKColors.White);
        return (accent, text);
    }

    private IReadOnlyList<SteamGameItem> FilterGames(IReadOnlyList<SteamGameItem> games)
    {
        if (ShowUninstalled)
            return games;

        List<SteamGameItem> installed = [];
        foreach (SteamGameItem game in games)
        {
            if (game.State != SteamGameState.Uninstalled)
                installed.Add(game);
        }

        return installed;
    }

    private (SteamGameItem? Focus, SteamGameItem? Previous, SteamGameItem? Next) ShowcaseSelection(IReadOnlyList<SteamGameItem> games)
    {
        if (games.Count == 0)
            return (null, null, null);

        _showcaseIndex = Math.Clamp(_showcaseIndex, 0, games.Count - 1);
        SteamGameItem? previous = _showcaseIndex > 0 ? games[_showcaseIndex - 1] : null;
        SteamGameItem? next = _showcaseIndex < games.Count - 1 ? games[_showcaseIndex + 1] : null;
        return (games[_showcaseIndex], previous, next);
    }

    private void SnapShowcase(int direction)
    {
        IReadOnlyList<SteamGameItem> games = FilterGames(_snapshot.Games);
        _showcaseIndex = Math.Clamp(_showcaseIndex + direction, 0, Math.Max(0, games.Count - 1));
        Context?.RequestRender();
    }

    private void ScrollGrid(float dragDelta)
    {
        SKRect bounds = _lastBounds.Width > 0f ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
        int columns = SteamGridLayout.ColumnsForWidth(bounds.Width);
        float gap = 12f * (bounds.Width / SteamGridLayout.DesignWidth);
        float cellWidth = (bounds.Width - 16f * 2f * (bounds.Width / SteamGridLayout.DesignWidth) - gap * (columns - 1)) / columns;
        int visibleCells = columns * Math.Max(1, (int)((bounds.Height - 32f * (bounds.Width / SteamGridLayout.DesignWidth)) / (cellWidth * SteamGridLayout.PosterAspect + gap)));
        float maxScroll = Math.Max(0f, (FilterGames(_snapshot.Games).Count - visibleCells) * (cellWidth + gap));
        _scrollX = Math.Clamp(_scrollX - dragDelta, 0f, maxScroll);
        Context?.RequestRender();
    }

    private void TapAt(SKPoint point, SteamLibraryService service)
    {
        IReadOnlyList<SteamGameItem> games = FilterGames(_snapshot.Games);
        if (games.Count == 0)
            return;

        if (IsShowcase)
        {
            (SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next) = ShowcaseSelection(games);
            SteamShowcaseGeometry geometry = SteamShowcaseLayout.Compute(_lastBounds, previous is not null, next is not null);
            switch (SteamShowcaseLayout.HitTest(geometry, point))
            {
                case SteamShowcaseHit.Previous:
                    SnapShowcase(-1);
                    break;
                case SteamShowcaseHit.Next:
                    SnapShowcase(1);
                    break;
                case SteamShowcaseHit.Focus:
                    if (focus is not null && focus.IsLaunchable)
                        service.Tracker.Launch(focus.AppId);
                    break;
            }
            return;
        }

        SKRect bounds = _lastBounds.Width > 0f ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
        int columns = SteamGridLayout.ColumnsForWidth(bounds.Width);
        SteamGridGeometry gridGeometry = SteamGridLayout.Compute(bounds, columns, _scrollX);
        int index = SteamGridLayout.HitTest(gridGeometry, point);
        if (index >= 0 && index < games.Count && games[index].IsLaunchable)
            service.Tracker.Launch(games[index].AppId);
    }

    private void ToggleFavoriteAt(SKPoint point, SteamLibraryService service)
    {
        IReadOnlyList<SteamGameItem> games = FilterGames(_snapshot.Games);
        if (games.Count == 0)
            return;

        uint? appId = null;
        if (IsShowcase)
        {
            (SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next) = ShowcaseSelection(games);
            SteamShowcaseGeometry geometry = SteamShowcaseLayout.Compute(_lastBounds, previous is not null, next is not null);
            if (SteamShowcaseLayout.HitTest(geometry, point) == SteamShowcaseHit.Focus && focus is not null)
                appId = focus.AppId;
        }
        else
        {
            SKRect bounds = _lastBounds.Width > 0f ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
            int columns = SteamGridLayout.ColumnsForWidth(bounds.Width);
            SteamGridGeometry geometry = SteamGridLayout.Compute(bounds, columns, _scrollX);
            int index = SteamGridLayout.HitTest(geometry, point);
            if (index >= 0 && index < games.Count)
                appId = games[index].AppId;
        }

        if (appId is not { } id)
            return;

        var set = new HashSet<uint>(ParseFavorites(FavoritesJson));
        if (!set.Remove(id))
            set.Add(id);

        string json = SerializeFavorites(set);
        service.SetFavoritesJson(json);
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

    private void DrawNotReady(SKCanvas canvas, SKRect bounds)
    {
        using var paint = new SKPaint
        {
            Color = new SKColor(18, 20, 24, 255),
            IsAntialias = true,
        };
        canvas.DrawRoundRect(bounds, 12f, 12f, paint);
    }

    internal static HashSet<uint> ParseFavorites(string json)
    {
        var ids = new HashSet<uint>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.TryGetUInt32(out uint id))
                    ids.Add(id);
            }
        }
        catch (Exception)
        {
            // A malformed favorites value reads as an empty set.
            return ids;
        }

        return ids;
    }

    internal static string SerializeFavorites(IEnumerable<uint> ids)
    {
        List<uint> distinct = [.. ids.Distinct()];
        return JsonSerializer.Serialize(distinct);
    }
}
