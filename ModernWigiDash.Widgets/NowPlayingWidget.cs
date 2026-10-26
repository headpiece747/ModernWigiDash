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
    private readonly NowPlayingCanvas _canvas = new();
    private bool _disposed;

    /// <summary>Production constructor; the media monitor and the canvas are bound in InitializeAsync over the context.</summary>
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

    /// <summary>Test seam: access to the last computed layout geometry.</summary>
    internal NowPlayingGeometry LastLayout => _canvas.LastLayout;

    /// <summary>
    /// Binds the context, initializes the canvas and the media session monitor over it,
    /// and starts the SMTC bootstrap.
    /// </summary>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        _canvas.Initialize(Context.LogError);
        _canvas.ArtworkChanged += OnArtworkChanged;
        _mediaMonitor = (_monitorFactory ?? (() => new MediaSessionMonitor(Context.LogError)))();
        _mediaMonitor.SnapshotChanged += OnMediaSnapshotChanged;
        _ = _mediaMonitor.InitializeAsync();
    }

    private void OnMediaSnapshotChanged(MediaSessionUpdate? update)
        => _canvas.NotifySnapshotChanged(update);

    private void OnArtworkChanged()
        => Context?.RequestRender();

    /// <summary>
    /// Draws the now-playing view or idle panel via <see cref="NowPlayingCanvas"/>.
    /// </summary>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _canvas.Render(
            canvas,
            bounds,
            _mediaMonitor?.CurrentSnapshot,
            AccentColorHex,
            TextColorHex,
            ShowSourceBadge,
            Clock.GetUtcNow());
    }

    /// <summary>
    /// Routes a tap to the hit-tested control via <see cref="NowPlayingCanvas"/>.
    /// </summary>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
        => _canvas.OnTouch(localPoint, eventType, _mediaMonitor);

    /// <summary>Unsubscribes, disposes the media monitor and the canvas resources.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _canvas.ArtworkChanged -= OnArtworkChanged;
        _canvas.Dispose();

        if (_mediaMonitor is not null)
        {
            _mediaMonitor.SnapshotChanged -= OnMediaSnapshotChanged;
            await _mediaMonitor.DisposeAsync().ConfigureAwait(false);
            _mediaMonitor = null;
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
