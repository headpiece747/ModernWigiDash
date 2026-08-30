namespace ModernWigiDash.Widgets;

/// <summary>
/// Live "Now Playing" media widget driven entirely by Windows media sessions
/// (System Media Transport Controls / GlobalSystemMediaTransportControlsSessionManager).
/// Covers Spotify, browsers (YouTube/Netflix), VLC, iTunes, Windows Media Player, games —
/// zero polling, zero network, zero login, works for free-tier accounts.
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

    // The icon paths are pure geometry of their button rects, which change
    // only when the widget resizes; rebuilt once per rect instead of per
    // frame. All five button rects derive from the same placement scale, so
    // the shuffle rect keys the rebuild.
    private SKRect _iconPathKeyRect;
    private SKPath? _shuffleCurves;
    private SKPath? _shuffleTopArrow;
    private SKPath? _shuffleBottomArrow;
    private SKPath? _prevTriangle;
    private SKPath? _playTriangle;
    private SKPath? _nextTriangle;
    private SKPath? _repeatArrow;

    // Hoisted paints: the colors mutate per render (property/snapshot-driven),
    // so the 30 FPS render allocates no SKPaint.
    private readonly SKPaint _bgPaint = new() { IsAntialias = true };
    private readonly SKPaint _idleIconPaint = new() { IsAntialias = true };
    private readonly SKPaint _idleLabelPaint = new() { IsAntialias = true };
    private readonly SKPaint _pillBgPaint = new() { Color = new SKColor(255, 255, 255, 25), IsAntialias = true };
    private readonly SKPaint _pillBorderPaint = new() { Color = new SKColor(255, 255, 255, 45), Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _statusDotPaint = new() { IsAntialias = true };
    private readonly SKPaint _badgeTextPaint = new() { IsAntialias = true };
    private readonly SKPaint _shadowPaint = new() { Color = new SKColor(0, 0, 0, 110), IsAntialias = true };
    private readonly SKPaint _artFillPaint = new() { IsAntialias = true };
    private readonly SKPaint _artIconPaint = new() { IsAntialias = true };
    private readonly SKPaint _artBorderPaint = new() { Color = new SKColor(255, 255, 255, 45), Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _titlePaint = new() { IsAntialias = true };
    private readonly SKPaint _artistPaint = new() { IsAntialias = true };
    private readonly SKPaint _albumTextPaint = new() { IsAntialias = true };
    private readonly SKPaint _metaPaint = new() { IsAntialias = true };
    private readonly SKPaint _timePaint = new() { IsAntialias = true };
    private readonly SKPaint _progressTrackPaint = new() { Color = new SKColor(255, 255, 255, 35), Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    private readonly SKPaint _progressFillPaint = new() { Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    private readonly SKPaint _progressDotPaint = new() { IsAntialias = true };
    private readonly SKPaint _progressDotCorePaint = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint _shufflePaint = new() { IsAntialias = true };
    private readonly SKPaint _prevPaint = new() { IsAntialias = true };
    private readonly SKPaint _nextPaint = new() { IsAntialias = true };
    private readonly SKPaint _repeatPaint = new() { IsAntialias = true };
    private readonly SKPaint _shuffleStrokePaint = new() { Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    private readonly SKPaint _repeatPenPaint = new() { Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    private readonly SKPaint _heroBgPaint = new() { IsAntialias = true };
    private readonly SKPaint _heroGlowPaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _heroIconPaint = new() { Color = new SKColor(18, 18, 24), IsAntialias = true };
    private readonly SKPaint _repeatNumPaint = new() { IsAntialias = true };

    // The album-art clip path is caller-owned and rebuilt in place (the
    // PictureAndGif precedent): SKPathBuilder's Snapshot()/Detach() would
    // allocate a new SKPath per frame.
    private SKPath? _albumClipPath;
    private SKRect _albumClipRect;
    private float _albumClipRadius = -1f;

    private static readonly SKSamplingOptions HighQualitySampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

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
    /// layout record, or the idle panel when no session is playing.
    /// </summary>
    /// <param name="canvas">The frame canvas.</param>
    /// <param name="bounds">The widget's placement bounds.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _artworkLoader?.DisposeRetired();

        float scale = Math.Min(bounds.Width / NowPlayingLayout.DesignWidth, bounds.Height / NowPlayingLayout.DesignHeight);

        // Background panel tinted by artwork-derived color
        var artState = _artworkLoader?.Current;
        var bgColor = NowPlayingLayout.BlendToward(artState?.BackgroundColor ?? new SKColor(18, 18, 24), new SKColor(18, 18, 24), 0.25f);
        _bgPaint.Color = bgColor;
        canvas.DrawRoundRect(bounds, 18f * scale, 18f * scale, _bgPaint);

        var snap = _mediaMonitor?.CurrentSnapshot;
        if (NowPlayingPresentation.IsIdle(snap))
        {
            DrawIdle(canvas, bounds, scale);
            return;
        }

        _layout = NowPlayingLayout.Compute(bounds, scale, ShowSourceBadge, MeasureBadgeTextWidth(snap, scale));
        EnsureIconPaths(_layout);

        DrawAlbumArt(canvas, bounds, scale);
        DrawSourceBadge(canvas, snap, scale);
        DrawTextInfo(canvas, bounds, snap, scale);
        DrawProgress(canvas, snap, scale);
        DrawControls(canvas, snap, scale);
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

    private void DrawIdle(SKCanvas canvas, SKRect bounds, float scale)
    {
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, 64f * scale);
        _idleIconPaint.Color = accent.WithAlpha(200);
        var tb = new SKRect();
        iconFont.MeasureText("🎵", out tb, _idleIconPaint);
        canvas.DrawTextWithFallback("🎵", bounds.MidX - tb.MidX, bounds.MidY - 24f * scale, iconFont, _idleIconPaint);

        var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 22f * scale);
        _idleLabelPaint.Color = ColorOf(TextColorHex, SKColors.White).WithAlpha(180);
        string hint = "No media playing — press play in any app";
        var lb = new SKRect();
        labelFont.MeasureText(hint, out lb, _idleLabelPaint);
        canvas.DrawTextWithFallback(hint, bounds.MidX - (lb.Width / 2f), bounds.MidY + 30f * scale, labelFont, _idleLabelPaint);
    }

    private void DrawSourceBadge(SKCanvas canvas, MediaSnapshot snap, float scale)
    {
        if (!_layout.SourceBadgeVisible) return;

        var pill = _layout.SourceBadgeRect;
        float h = pill.Height;
        float x = pill.Left;
        string name = NowPlayingPresentation.FriendlyAppName(snap.SourceAppId);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f * scale);

        canvas.DrawRoundRect(pill, h / 2f, h / 2f, _pillBgPaint);

        _pillBorderPaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(pill, h / 2f, h / 2f, _pillBorderPaint);

        _statusDotPaint.Color = snap.IsPlaying ? new SKColor(34, 197, 94) : new SKColor(239, 68, 68);
        canvas.DrawCircle(x + 11f * scale, pill.MidY, 3.5f * scale, _statusDotPaint);

        _badgeTextPaint.Color = ColorOf(TextColorHex, SKColors.White);
        canvas.DrawTextWithFallback(name, x + 18f * scale, pill.MidY - font.Metrics.Top * 0.42f - 1f * scale, font, _badgeTextPaint);
    }

    private void DrawAlbumArt(SKCanvas canvas, SKRect bounds, float scale)
    {
        float pad = _layout.Pad;
        // Equal spacing pad from top, left, and bottom
        float artSide = _layout.ArtSide;
        float artTop = bounds.Top + pad + Math.Max(0f, (bounds.Height - pad * 2f - artSide) / 2f);
        var artRect = new SKRect(bounds.Left + pad, artTop,
                                 bounds.Left + pad + artSide, artTop + artSide);

        float r = 16f * scale;
        float shadowOff = 6f * scale;
        canvas.DrawRoundRect(new SKRect(artRect.Left + shadowOff, artRect.Top + shadowOff,
                                        artRect.Right + shadowOff, artRect.Bottom + shadowOff), r, r, _shadowPaint);

        // Snapshot the artwork once: background SMTC refreshes can replace (or
        // null) the published artwork between two reads, which would NRE the
        // render mid-draw. The retired-list discipline guarantees the snapshot
        // stays alive for this draw even if a refresh retires it right after.
        var art = _artworkLoader?.Current.Bitmap;
        if (art is not null)
        {
            canvas.Save();
            EnsureAlbumClipPath(artRect, r);
            canvas.ClipPath(_albumClipPath);
            canvas.DrawBitmap(art, artRect, HighQualitySampling);
            canvas.Restore();
        }
        else
        {
            _artFillPaint.Color = ColorOf(AccentColorHex, WidgetPalette.Accent).WithAlpha(80);
            canvas.DrawRoundRect(artRect, r, r, _artFillPaint);

            var font = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, artSide * 0.45f);
            _artIconPaint.Color = SKColors.White.WithAlpha(220);
            var tb = new SKRect();
            font.MeasureText("🎵", out tb, _artIconPaint);
            canvas.DrawTextWithFallback("🎵", artRect.MidX - tb.MidX, artRect.MidY - tb.MidY, font, _artIconPaint);
        }

        _artBorderPaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(artRect, r, r, _artBorderPaint);
    }

    /// <summary>
    /// Rebuilds the caller-owned album clip path in place when the art rect or
    /// radius changes (a resize or a corner-radius edit). The clip is applied
    /// every frame, but the geometry changes only on those two inputs.
    /// </summary>
    private void EnsureAlbumClipPath(SKRect rect, float radius)
    {
        if (_albumClipPath is not null && _albumClipRect == rect
            && BitConverter.SingleToInt32Bits(_albumClipRadius) == BitConverter.SingleToInt32Bits(radius))
        {
            return;
        }

        _albumClipRect = rect;
        _albumClipRadius = radius;
        _albumClipPath ??= new SKPath();
#pragma warning disable CS0618 // SKPath.Rewind/AddRoundRect are obsolete in favor of SKPathBuilder, whose Snapshot() allocates a new SKPath per call — the clip path object is reused and rebuilt instead (zero-alloc hot path).
        _albumClipPath.Rewind();
        _albumClipPath.AddRoundRect(rect, radius, radius);
#pragma warning restore CS0618
    }

    private void DrawTextInfo(SKCanvas canvas, SKRect bounds, MediaSnapshot snap, float scale)
    {
        float pad = _layout.Pad;
        float artSide = _layout.ArtSide;
        // The text column shares the progress band's left edge — one column,
        // one X, from the layout record (the layout's barW is right - left).
        float textX = _layout.ProgressLeft;
        float textW = bounds.Right - pad - textX;
        if (textW <= 0) return;

        // Shift text stack down approx 3 lines total from top pad (2 lines lower than before)
        float textTop = bounds.Top + pad + Math.Max(0f, (artSide - 160f * scale) / 2f);
        SKColor text = ColorOf(TextColorHex, SKColors.White);
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);

        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 40f * scale);
        var artistFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 28f * scale);
        var albumFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 22f * scale);
        var metaFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 18f * scale);

        _titlePaint.Color = text;
        _artistPaint.Color = text.WithAlpha(230);
        _albumTextPaint.Color = text.WithAlpha(180);
        _metaPaint.Color = accent;

        float titleH = titleFont.Metrics.Bottom - titleFont.Metrics.Top;
        float artistH = artistFont.Metrics.Bottom - artistFont.Metrics.Top;
        float albumH = albumFont.Metrics.Bottom - albumFont.Metrics.Top;

        canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(IsEmpty(snap.Title) ? "Unknown Title" : snap.Title, titleFont, textW),
                        textX, textTop - titleFont.Metrics.Top, titleFont, _titlePaint);

        float currentY = textTop + titleH + 6f * scale;

        if (!IsEmpty(snap.Artist))
        {
            canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(snap.Artist, artistFont, textW), textX, currentY - artistFont.Metrics.Top, artistFont, _artistPaint);
            currentY += artistH + 5f * scale;
        }

        if (!IsEmpty(snap.Album))
        {
            canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(snap.Album, albumFont, textW), textX, currentY - albumFont.Metrics.Top, albumFont, _albumTextPaint);
            currentY += albumH + 5f * scale;
        }

        string meta = NowPlayingPresentation.MetaLine(snap.TrackNumber, snap.AlbumTrackCount, snap.Genres);
        if (!string.IsNullOrEmpty(meta))
        {
            canvas.DrawTextWithFallback(meta, textX, currentY - metaFont.Metrics.Top, metaFont, _metaPaint);
        }
    }

    private void DrawProgress(SKCanvas canvas, MediaSnapshot snap, float scale)
    {
        float left = _layout.ProgressLeft;
        float barY = _layout.ProgressY;
        float timeY = barY - 18f * scale;
        float barW = _layout.ProgressWidth;
        if (barW <= 0) return;
        // The column's right edge is one fact: the band spans exactly
        // left..left+barW (the layout computed barW as right - left).
        float right = left + barW;

        double durSec = snap.Duration.TotalSeconds;
        double posSec = NowPlayingPresentation.ExtrapolatedPosition(snap, Clock.GetUtcNow());

        double ratio = NowPlayingPresentation.ProgressRatio(posSec, durSec);
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);

        // Time labels above progress bar track
        var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 16f * scale);
        _timePaint.Color = ColorOf(TextColorHex, SKColors.White).WithAlpha(210);
        canvas.DrawTextWithFallback(NowPlayingPresentation.FormatTime(Math.Clamp(posSec, 0, Math.Max(0, durSec))), left, timeY, timeFont, _timePaint);

        string durStr = NowPlayingPresentation.FormatTime(durSec);
        var db = new SKRect();
        timeFont.MeasureText(durStr, out db, _timePaint);
        canvas.DrawTextWithFallback(durStr, right - db.Width, timeY, timeFont, _timePaint);

        if (NowPlayingPresentation.PlaybackRateText(snap.PlaybackRate) is { } rate)
        {
            canvas.DrawTextWithFallback(rate, left + db.Width + 20f * scale, timeY, timeFont, _timePaint);
        }

        // Background progress track
        _progressTrackPaint.StrokeWidth = 7f * scale;
        canvas.DrawLine(left, barY, right, barY, _progressTrackPaint);

        if (ratio > 0)
        {
            _progressFillPaint.Color = accent;
            _progressFillPaint.StrokeWidth = 7f * scale;
            canvas.DrawLine(left, barY, left + barW * (float)ratio, barY, _progressFillPaint);

            float dotR = 9f * scale;
            float dotX = left + barW * (float)ratio;
            _progressDotPaint.Color = accent;
            canvas.DrawCircle(dotX, barY, dotR, _progressDotPaint);
            canvas.DrawCircle(dotX, barY, 4f * scale, _progressDotCorePaint);
        }
    }

    private void DrawControls(SKCanvas canvas, MediaSnapshot snap, float scale)
    {
        SKColor text = ColorOf(TextColorHex, SKColors.White);
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);

        bool repeatActive = snap.Repeat != MediaRepeatMode.None;
        bool canPp = snap.IsPlaying ? snap.CanPause : snap.CanPlay;

        // One paint set per frame for all five controls; the icon geometry
        // itself is cached (EnsureIconPaths) and rebuilt only on resize.
        _shufflePaint.Color = IconColor(snap.Shuffle, snap.CanShuffle, accent, text);
        _prevPaint.Color = IconColor(false, snap.CanPrev, accent, text);
        _nextPaint.Color = IconColor(false, snap.CanNext, accent, text);
        _repeatPaint.Color = IconColor(repeatActive, snap.CanRepeat, accent, text);

        _shuffleStrokePaint.Color = _shufflePaint.Color;
        _shuffleStrokePaint.StrokeWidth = _layout.ShuffleButton.Width * 0.07f;
        _repeatPenPaint.Color = _repeatPaint.Color;
        _repeatPenPaint.StrokeWidth = _layout.RepeatButton.Width * 0.07f;

        _heroBgPaint.Color = accent.WithAlpha(canPp ? (byte)245 : (byte)100);
        _heroGlowPaint.Color = accent.WithAlpha(canPp ? (byte)90 : (byte)20);
        _heroGlowPaint.StrokeWidth = 2f * scale;

        // Shuffle (Clean icon button without glass circle)
        DrawShuffleIcon(canvas, _shufflePaint, _shuffleStrokePaint);

        // Prev (Clean icon button without glass circle)
        DrawPrevIcon(canvas, _layout.PreviousButton, _prevPaint);

        // Play / Pause (Hero Glowing Accent Button)
        DrawHeroPlayButton(canvas, _layout.PlayPauseButton, scale, _heroBgPaint, _heroGlowPaint, _heroIconPaint, snap.IsPlaying);

        // Next (Clean icon button without glass circle)
        DrawNextIcon(canvas, _layout.NextButton, _nextPaint);

        // Repeat (Clean icon button without glass circle)
        DrawRepeatIcon(canvas, _layout.RepeatButton, _repeatPaint, _repeatPenPaint, snap.Repeat == MediaRepeatMode.Track);
    }

    private static SKColor IconColor(bool active, bool enabled, SKColor accent, SKColor text)
    {
        if (active) return accent;
        return text.WithAlpha(enabled ? (byte)240 : (byte)70);
    }

    private void DrawHeroPlayButton(SKCanvas canvas, SKRect r, float scale, SKPaint btnBg, SKPaint glowBorder, SKPaint iconPaint, bool isPlaying)
    {
        // Hero Play button: Solid accent fill circular button
        canvas.DrawOval(r, btnBg);

        // Outer glow ring
        float glowOff = 4f * scale;
        var glowRect = new SKRect(r.Left - glowOff, r.Top - glowOff, r.Right + glowOff, r.Bottom + glowOff);
        canvas.DrawOval(glowRect, glowBorder);

        // High contrast dark icon inside play button
        if (isPlaying)
            DrawPauseIcon(canvas, r, iconPaint);
        else
            DrawPlayIcon(canvas, iconPaint);
    }

    // The icon paths are cached per layout rect (EnsureIconPaths); these
    // methods only draw the cached geometry with the frame's paints.

    private void DrawPrevIcon(SKCanvas canvas, SKRect r, SKPaint paint)
    {
        float cx = r.MidX, cy = r.MidY;
        float h = r.Height * 0.32f;
        float barW = r.Width * 0.08f;

        // Solid vertical bar
        var barRect = new SKRect(cx - r.Width * 0.22f, cy - h, cx - r.Width * 0.22f + barW, cy + h);
        canvas.DrawRoundRect(barRect, barW / 2f, barW / 2f, paint);

        // Smooth rounded triangle (cached path)
        canvas.DrawPath(_prevTriangle, paint);
    }

    private void DrawPlayIcon(SKCanvas canvas, SKPaint paint)
    {
        // Cached triangle path
        canvas.DrawPath(_playTriangle, paint);
    }

    private static void DrawPauseIcon(SKCanvas canvas, SKRect r, SKPaint paint)
    {
        float cx = r.MidX, cy = r.MidY;
        float w = r.Width * 0.09f;
        float h = r.Height * 0.32f;
        float gap = r.Width * 0.12f;

        var left = new SKRect(cx - gap / 2f - w, cy - h, cx - gap / 2f, cy + h);
        var right = new SKRect(cx + gap / 2f, cy - h, cx + gap / 2f + w, cy + h);
        canvas.DrawRoundRect(left, w / 2f, w / 2f, paint);
        canvas.DrawRoundRect(right, w / 2f, w / 2f, paint);
    }

    private void DrawNextIcon(SKCanvas canvas, SKRect r, SKPaint paint)
    {
        float cx = r.MidX, cy = r.MidY;
        float h = r.Height * 0.32f;
        float barW = r.Width * 0.08f;

        // Smooth rounded triangle (cached path)
        canvas.DrawPath(_nextTriangle, paint);

        // Solid vertical bar
        var barRect = new SKRect(cx + r.Width * 0.22f - barW, cy - h, cx + r.Width * 0.22f, cy + h);
        canvas.DrawRoundRect(barRect, barW / 2f, barW / 2f, paint);
    }

    private void DrawShuffleIcon(SKCanvas canvas, SKPaint paint, SKPaint stroke)
    {
        // S-curves (cached path), then the arrowheads (cached paths)
        canvas.DrawPath(_shuffleCurves, stroke);
        canvas.DrawPath(_shuffleTopArrow, paint);
        canvas.DrawPath(_shuffleBottomArrow, paint);
    }

    private void DrawRepeatIcon(SKCanvas canvas, SKRect r, SKPaint paint, SKPaint pen, bool repeatOne)
    {
        float cx = r.MidX, cy = r.MidY;
        float outer = r.Width * 0.22f;

        var oval = new SKRect(cx - outer, cy - outer, cx + outer, cy + outer);
        canvas.DrawArc(oval, 55f, 250f, false, pen);

        // Arrowhead (cached path)
        canvas.DrawPath(_repeatArrow, paint);

        if (repeatOne)
        {
            var numFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, r.Width * 0.24f);
            _repeatNumPaint.Color = paint.Color;
            numFont.MeasureText("1", out var nb, _repeatNumPaint);
            canvas.DrawTextWithFallback("1", cx - nb.Width / 2f, cy + nb.Height / 3f, numFont, _repeatNumPaint);
        }
    }

    /// <summary>
    /// Rebuilds the cached control-icon paths when the layout rects change
    /// (widget resize). The button rects are pure functions of the placement
    /// bounds and scale, so the shuffle rect alone keys the rebuild.
    /// </summary>
    private void EnsureIconPaths(NowPlayingGeometry layout)
    {
        if (_shuffleCurves is not null && NowPlayingLayout.SameRect(_iconPathKeyRect, layout.ShuffleButton))
        {
            return;
        }

        _iconPathKeyRect = layout.ShuffleButton;
        DisposeIconPaths();

        _shuffleCurves = NowPlayingLayout.BuildShuffleCurves(layout.ShuffleButton);
        _shuffleTopArrow = NowPlayingLayout.BuildShuffleArrow(layout.ShuffleButton, top: true);
        _shuffleBottomArrow = NowPlayingLayout.BuildShuffleArrow(layout.ShuffleButton, top: false);
        _prevTriangle = NowPlayingLayout.BuildPrevTriangle(layout.PreviousButton);
        _playTriangle = NowPlayingLayout.BuildPlayTriangle(layout.PlayPauseButton);
        _nextTriangle = NowPlayingLayout.BuildNextTriangle(layout.NextButton);
        _repeatArrow = NowPlayingLayout.BuildRepeatArrow(layout.RepeatButton);
    }

    private void DisposeIconPaths()
    {
        _shuffleCurves?.Dispose();
        _shuffleTopArrow?.Dispose();
        _shuffleBottomArrow?.Dispose();
        _prevTriangle?.Dispose();
        _playTriangle?.Dispose();
        _nextTriangle?.Dispose();
        _repeatArrow?.Dispose();
        _shuffleCurves = null;
        _shuffleTopArrow = null;
        _shuffleBottomArrow = null;
        _prevTriangle = null;
        _playTriangle = null;
        _nextTriangle = null;
        _repeatArrow = null;
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

    private static bool IsEmpty(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>Unsubscribes, disposes the media monitor and the artwork loader, and releases the widget's Skia surfaces.</summary>
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeIconPaths();
        _albumClipPath?.Dispose();
        _albumClipPath = null;

        _bgPaint.Dispose();
        _idleIconPaint.Dispose();
        _idleLabelPaint.Dispose();
        _pillBgPaint.Dispose();
        _pillBorderPaint.Dispose();
        _statusDotPaint.Dispose();
        _badgeTextPaint.Dispose();
        _shadowPaint.Dispose();
        _artFillPaint.Dispose();
        _artIconPaint.Dispose();
        _artBorderPaint.Dispose();
        _titlePaint.Dispose();
        _artistPaint.Dispose();
        _albumTextPaint.Dispose();
        _metaPaint.Dispose();
        _timePaint.Dispose();
        _progressTrackPaint.Dispose();
        _progressFillPaint.Dispose();
        _progressDotPaint.Dispose();
        _progressDotCorePaint.Dispose();
        _shufflePaint.Dispose();
        _prevPaint.Dispose();
        _nextPaint.Dispose();
        _repeatPaint.Dispose();
        _shuffleStrokePaint.Dispose();
        _repeatPenPaint.Dispose();
        _heroBgPaint.Dispose();
        _heroGlowPaint.Dispose();
        _heroIconPaint.Dispose();
        _repeatNumPaint.Dispose();

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
