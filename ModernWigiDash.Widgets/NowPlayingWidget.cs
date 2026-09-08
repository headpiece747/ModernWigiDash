namespace ModernWigiDash.Widgets;

/// <summary>
/// Live "Now Playing" media widget driven entirely by Windows media sessions
/// (System Media Transport Controls / GlobalSystemMediaTransportControlsSessionManager).
/// Covers Spotify, browsers (YouTube/Netflix), VLC, iTunes, Windows Media Player, games —
/// zero polling, zero network, zero login, works for free-tier accounts.
/// The widget keeps only orchestration: the monitor/artwork lifecycle, the frame
/// inputs, and the touch routing. Every paint decision lives in
/// <see cref="NowPlayingRenderer"/> (the WeatherWidgetRenderer precedent), so the
/// drawn scene is testable through the renderer's interface without a widget instance.
/// </summary>
[WidgetMetadata("now_playing", "Now Playing", Category = "Media & Audio", DefaultGridSize = GridSizePreset.Size5x4)]
public sealed class NowPlayingWidget : ModernWidgetBase
{
    /// <summary>The "Accent Color" property: progress fill, active toggles, and placeholder accent.</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Progress fill, active toggles, and placeholder accent", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Text Color" property: title, artist, and icon color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Title, artist, and icon color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    /// <summary>The "Show Source Badge" property: show which app is playing (tap to switch sources).</summary>
    [WidgetProperty("Show Source Badge", WidgetPropertyType.Boolean, "Show which app is playing (tap to switch sources)", true)]
    public bool ShowSourceBadge { get; set; } = true;

    // ── SMTC state (all mutated on the UI thread) ─────────────────────────
    private readonly Func<MediaSessionMonitor>? _monitorFactory;
    private MediaSessionMonitor? _mediaMonitor;
    private ArtworkLoader? _artworkLoader;
    private SKPoint? _touchDownPoint;
    private bool _disposed;

    /// <summary>Production constructor; the media monitor and the artwork loader are created in InitializeAsync over the context.</summary>
    public NowPlayingWidget()
    {
    }

    /// <summary>Test seam: inject a monitor factory (e.g. over a fake SMTC source).</summary>
    internal NowPlayingWidget(Func<MediaSessionMonitor> monitorFactory)
    {
        _monitorFactory = monitorFactory;
    }

    /// <summary>Test seam: injectable clock for the progress estimate.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    // One layout record per frame: Render draws from it, OnTouch hit-tests
    // the same record, so the drawn controls and the tap targets can never
    // drift apart.
    private NowPlayingGeometry _layout;

    // The draw module owns every paint, hoisted paint, cached icon path, and
    // the album clip path; the widget disposes it at teardown.
    private readonly NowPlayingRenderer _renderer = new();

    /// <summary>
    /// Binds the context, creates the artwork loader and the media session
    /// monitor over it, and starts the SMTC bootstrap.
    /// </summary>
    /// <param name="context">The widget host context.</param>
    /// <param name="cancellationToken">Cancels the initialization.</param>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        _artworkLoader = new ArtworkLoader(Context.LogError);
        _artworkLoader.ArtworkChanged += OnArtworkChanged;
        _mediaMonitor = (_monitorFactory ?? (() => new MediaSessionMonitor(Context.LogError)))();
        _mediaMonitor.SnapshotChanged += OnMediaSnapshotChanged;
        _ = _mediaMonitor.InitializeAsync();
    }

    /// <summary>
    /// Forwards snapshot updates to the <see cref="ArtworkLoader"/>, which owns
    /// key-change detection, the reload decision, the load pipeline, and the
    /// retire-and-publish discipline. The loader raises <see cref="ArtworkLoader.ArtworkChanged"/>
    /// after each completed load (success, skipped, or failed).
    /// </summary>
    private void OnMediaSnapshotChanged(MediaSessionUpdate? update)
    {
        _artworkLoader?.NotifySnapshotChanged(update);
    }

    private void OnArtworkChanged()
    {
        Context?.RequestRender();
    }

    /// <summary>
    /// Draws the now-playing view (artwork, title/artist meta, progress, and
    /// the control row) from the monitor's latest snapshot and the per-frame
    /// layout record, or the idle panel when no session is playing. All paints
    /// route through <see cref="NowPlayingRenderer"/>.
    /// </summary>
    /// <param name="canvas">The frame canvas.</param>
    /// <param name="bounds">The widget's placement bounds.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _artworkLoader?.DisposeRetired();

        float scale = Math.Min(bounds.Width / NowPlayingLayout.DesignWidth, bounds.Height / NowPlayingLayout.DesignHeight);

        var artState = _artworkLoader?.Current;
        _renderer.DrawBackground(canvas, bounds, scale, artState?.BackgroundColor);

        var snap = _mediaMonitor?.CurrentSnapshot;
        if (NowPlayingPresentation.IsIdle(snap))
        {
            _renderer.DrawIdle(canvas, bounds, scale, ColorOf(AccentColorHex, WidgetPalette.Accent), ColorOf(TextColorHex, SKColors.White));
            return;
        }

        _layout = NowPlayingLayout.Compute(bounds, scale, ShowSourceBadge, MeasureBadgeTextWidth(snap, scale));

        // Snapshot the artwork once before handing it to the renderer: background
        // SMTC refreshes can replace (or null) the published artwork between two
        // reads, which would NRE the render mid-draw. The retired-list discipline
        // guarantees the snapshot stays alive for this draw even if a refresh
        // retires it right after.
        _renderer.DrawActiveScene(
            canvas, bounds, scale, _layout, snap,
            artState?.Bitmap,
            ColorOf(AccentColorHex, WidgetPalette.Accent),
            ColorOf(TextColorHex, SKColors.White),
            Clock.GetUtcNow());
    }

    /// <summary>
    /// The badge label's measured width — the one font-dependent input to the
    /// frame layout. Measured only when the badge is shown; the layout module
    /// computes the rect unconditionally but gates hit-testing on visibility.
    /// </summary>
    private float MeasureBadgeTextWidth(MediaSnapshot snap, float scale)
    {
        if (!ShowSourceBadge) return 0f;
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f * scale);
        return FontHelper.MeasureTextWithFallback(NowPlayingPresentation.FriendlyAppName(snap.SourceAppId), font);
    }

    /// <summary>
    /// Routes a tap to the hit-tested control (shuffle, previous, play/pause,
    /// next, repeat, source badge, or seek) as an intent to the media monitor.
    /// </summary>
    /// <param name="localPoint">The touch point in the widget's rotated-local space.</param>
    /// <param name="eventType">The touch event type.</param>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchDown)
        {
            _touchDownPoint = localPoint;
            return;
        }

        if (eventType != TouchEventType.TouchUp) return;
        var monitor = _mediaMonitor;
        if (monitor is null)
        {
            _touchDownPoint = null;
            return;
        }

        // Use the contact point rather than the release point so minor touch
        // movement does not turn a valid button press into a miss.
        SKPoint hitPoint = _touchDownPoint ?? localPoint;
        _touchDownPoint = null;

        // The widget sends intents; the monitor decides can-run and argument
        // from its own latest snapshot, so no tap path reads a snapshot the
        // widget held (a stale-toggle command is unrepresentable here). The
        // prev/next taps send a fixed command the session itself refuses
        // when its capability is off, so they need no veto of their own.
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

    /// <summary>Unsubscribes, disposes the media monitor and the artwork loader, and releases the widget's Skia surfaces.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _renderer.Dispose();

        if (_mediaMonitor is not null)
        {
            _mediaMonitor.SnapshotChanged -= OnMediaSnapshotChanged;
            await _mediaMonitor.DisposeAsync().ConfigureAwait(false);
            _mediaMonitor = null;
        }
        if (_artworkLoader is not null)
        {
            _artworkLoader.ArtworkChanged -= OnArtworkChanged;
            _artworkLoader.DisposeAll();
            _artworkLoader = null;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
