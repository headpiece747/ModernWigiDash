namespace ModernWigiDash.Widgets;

using SkiaSharp;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;

/// <summary>
/// Deep presentation and interaction canvas for the Now Playing widget.
/// Encapsulates layout computation, artwork loading and retirement lifecycle,
/// touch gesture dispatch, and Skia rendering behind a cohesive interface.
/// </summary>
internal sealed class NowPlayingCanvas : IDisposable
{
    private readonly NowPlayingRenderer _renderer = new();
    private ArtworkLoader? _artworkLoader;
    private NowPlayingGeometry _layout;
    private SKPoint? _touchDownPoint;

    // The geometry gate (the CalendarCanvas/CozyTodoCanvas model): the layout
    // is a pure function of (bounds, badge visibility, measured badge width),
    // so the 30 FPS tick re-computes it only when those inputs change. The key
    // compares the bounds bit-exact (the house float-bit pattern) so a one-ulp
    // drift never forces a needless recompute.
    private SKRect _layoutBoundsKey;
    private bool _layoutBadgeVisibleKey;
    private int _layoutBadgeWidthKeyBits;
    private bool _layoutKeyValid;
    private int _layoutComputations;

    // The badge label is a pure function of the source app id (the renderer's
    // app-name memo is a separate object); memoized here so the per-tick badge
    // width measurement does not reformat the app name.
    private string _badgeAppKey = "";
    private string _badgeAppName = "";

    /// <summary>Raised when album artwork finishes loading or updates.</summary>
    public event Action? ArtworkChanged;

    /// <summary>Test seam: the last computed frame geometry.</summary>
    internal NowPlayingGeometry LastLayout => _layout;

    /// <summary>
    /// Test seam: how many times the geometry gate has recomputed the layout
    /// (a steady-input tick must not grow this; a gate regression that
    /// recomputes every tick shows up here, which a geometry-equality check
    /// cannot see because the layout is pure arithmetic).
    /// </summary>
    internal int LayoutComputationsForTest => _layoutComputations;

    /// <summary>Initializes the canvas with a logging action and binds the artwork loader (a re-init retires the prior loader: its subscription is removed and its bitmaps disposed before the replacement).</summary>
    public void Initialize(Action<string, Exception?>? logError)
    {
        if (_artworkLoader is not null)
        {
            _artworkLoader.ArtworkChanged -= OnArtworkChanged;
            _artworkLoader.DisposeAll();
        }
        _artworkLoader = new ArtworkLoader(logError);
        _artworkLoader.ArtworkChanged += OnArtworkChanged;
    }

    private void OnArtworkChanged() => ArtworkChanged?.Invoke();

    /// <summary>Forwards media session snapshot changes to the internal artwork loader.</summary>
    public void NotifySnapshotChanged(MediaSessionUpdate? update)
        => _artworkLoader?.NotifySnapshotChanged(update);

    /// <summary>
    /// Measures layout, disposes retired artwork bitmaps, and draws the Now Playing scene.
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        MediaSnapshot? snap,
        string accentColorHex,
        string textColorHex,
        bool showSourceBadge,
        DateTimeOffset nowUtc)
    {
        _artworkLoader?.DisposeRetired();

        float scale = Math.Min(bounds.Width / NowPlayingLayout.DesignWidth, bounds.Height / NowPlayingLayout.DesignHeight);

        var artState = _artworkLoader?.Current;
        _renderer.DrawBackground(canvas, bounds, scale, artState?.BackgroundColor);

        if (NowPlayingPresentation.IsIdle(snap))
        {
            _renderer.DrawIdle(canvas, bounds, scale, SkColorCache.ColorOf(accentColorHex, WidgetPalette.Accent), SkColorCache.ColorOf(textColorHex, SKColors.White));
            return;
        }

        _layout = LayoutGate(bounds, showSourceBadge, MeasureBadgeTextWidth(snap, scale, showSourceBadge));

        _renderer.DrawActiveScene(
            canvas, bounds, scale, _layout, snap,
            artState?.Bitmap,
            SkColorCache.ColorOf(accentColorHex, WidgetPalette.Accent),
            SkColorCache.ColorOf(textColorHex, SKColors.White),
            nowUtc);
    }

    /// <summary>
    /// The geometry gate: the layout is a pure function of (bounds, badge
    /// visibility, measured badge width), so it is computed once and served
    /// from a single slot until one of those inputs changes (the key compares
    /// the bounds bit-exact, the house float-bit pattern).
    /// </summary>
    private NowPlayingGeometry LayoutGate(SKRect bounds, bool showSourceBadge, float badgeTextWidth)
    {
        int badgeWidthBits = BitConverter.SingleToInt32Bits(badgeTextWidth);
        if (_layoutKeyValid
            && NowPlayingLayout.SameRect(_layoutBoundsKey, bounds)
            && _layoutBadgeVisibleKey == showSourceBadge
            && _layoutBadgeWidthKeyBits == badgeWidthBits)
        {
            return _layout;
        }

        float scale = Math.Min(bounds.Width / NowPlayingLayout.DesignWidth, bounds.Height / NowPlayingLayout.DesignHeight);
        _layout = NowPlayingLayout.Compute(bounds, scale, showSourceBadge, badgeTextWidth);
        _layoutComputations++;
        _layoutBoundsKey = bounds;
        _layoutBadgeVisibleKey = showSourceBadge;
        _layoutBadgeWidthKeyBits = badgeWidthBits;
        _layoutKeyValid = true;
        return _layout;
    }

    private float MeasureBadgeTextWidth(MediaSnapshot snap, float scale, bool showSourceBadge)
    {
        if (!showSourceBadge) return 0f;
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f * scale);
        return FontHelper.MeasureTextWithFallback(BadgeAppName(snap.SourceAppId), font);
    }

    private string BadgeAppName(string appId)
    {
        if (string.Equals(_badgeAppKey, appId, StringComparison.Ordinal))
        {
            return _badgeAppName;
        }

        _badgeAppKey = appId;
        _badgeAppName = NowPlayingPresentation.FriendlyAppName(appId);
        return _badgeAppName;
    }

    /// <summary>
    /// Routes touch inputs to media controls (shuffle, prev, play/pause, next, repeat, source badge, seek).
    /// </summary>
    public void OnTouch(SKPoint localPoint, TouchEventType eventType, MediaSessionMonitor? monitor)
    {
        if (eventType == TouchEventType.TouchDown)
        {
            _touchDownPoint = localPoint;
            return;
        }

        if (eventType != TouchEventType.TouchUp || monitor is null)
        {
            if (eventType != TouchEventType.TouchDown) _touchDownPoint = null;
            return;
        }

        SKPoint hitPoint = _touchDownPoint ?? localPoint;
        _touchDownPoint = null;

        switch (NowPlayingLayout.GetAction(_layout, hitPoint))
        {
            case NowPlayingHitAction.Shuffle:
                monitor.ToggleShuffle();
                break;
            case NowPlayingHitAction.Previous:
                monitor.Previous();
                break;
            case NowPlayingHitAction.PlayPause:
                monitor.TogglePlayPause();
                break;
            case NowPlayingHitAction.Next:
                monitor.Next();
                break;
            case NowPlayingHitAction.Repeat:
                monitor.CycleRepeat();
                break;
            case NowPlayingHitAction.SourceBadge:
                monitor.CycleSession();
                break;
            case NowPlayingHitAction.Seek:
                double ratio = NowPlayingPresentation.SeekRatio(hitPoint.X, _layout.ProgressLeft, _layout.ProgressWidth);
                monitor.SeekToRatio(ratio);
                break;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _renderer.Dispose();
        if (_artworkLoader is not null)
        {
            _artworkLoader.ArtworkChanged -= OnArtworkChanged;
            _artworkLoader.DisposeAll();
            _artworkLoader = null;
        }
    }
}
