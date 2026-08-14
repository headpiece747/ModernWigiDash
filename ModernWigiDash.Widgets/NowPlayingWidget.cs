using Windows.Media;
using Windows.Media.Control;
using ModernWigiDash.Sdk;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Live "Now Playing" media widget driven entirely by Windows media sessions
/// (System Media Transport Controls / GlobalSystemMediaTransportControlsSessionManager).
/// Covers Spotify, browsers (YouTube/Netflix), VLC, iTunes, Windows Media Player, games —
/// zero polling, zero network, zero login, works for free-tier accounts.
/// </summary>
[WidgetMetadata("now_playing", "Now Playing", Category = "Media & Audio")]
public sealed class NowPlayingWidget : ModernWidgetBase
{
    public override SKSize DefaultSize => GridSizePreset.Size5x4.ToSize();

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Progress fill, active toggles, and placeholder accent", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Title, artist, and icon color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Show Source Badge", WidgetPropertyType.Boolean, "Show which app is playing (tap to switch sources)", true)]
    public bool ShowSourceBadge { get; set; } = true;

    // ── SMTC state (all mutated on the UI thread) ─────────────────────────
    private readonly Func<MediaSessionMonitor>? _monitorFactory;
    private MediaSessionMonitor? _mediaMonitor;
    private ArtworkLoader? _artworkLoader;
    private SKPoint? _touchDownPoint;
    private bool _disposed;

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

    // ── Frame geometry written during Render (used by OnTouch) ────────────
    // One layout record per frame: Render draws from it, OnTouch hit-tests
    // the same record, so the drawn controls and the tap targets can never
    // drift apart.
    private NowPlayingGeometry _layout;

    // ── Cached control-icon geometry ──────────────────────────────────────
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

    private static readonly SKSamplingOptions HighQualitySampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public override ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(context, cancellationToken);
        _artworkLoader = new ArtworkLoader(Context.LogError);
        _artworkLoader.ArtworkChanged += OnArtworkChanged;
        _mediaMonitor = (_monitorFactory ?? (() => new MediaSessionMonitor(Context.LogError)))();
        _mediaMonitor.SnapshotChanged += OnMediaSnapshotChanged;
        _ = _mediaMonitor.InitializeAsync();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Forwards snapshot updates to the <see cref="ArtworkLoader"/>, which owns
    /// key-change detection, the reload decision, the load pipeline, and the
    /// retire-and-publish discipline. The loader raises <see cref="ArtworkLoader.ArtworkChanged"/>
    /// after each completed load (success, skipped, or failed), so render
    /// requests land at the same point the old inline pipeline produced them.
    /// </summary>
    private void OnMediaSnapshotChanged(MediaSessionUpdate? update)
    {
        _artworkLoader?.NotifySnapshotChanged(update);
    }

    private void OnArtworkChanged(ArtworkLoaded? artwork)
    {
        Context?.RequestRender();
    }

    // ── Render ────────────────────────────────────────────────────────────

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _artworkLoader?.DisposeRetired();

        float scale = Math.Min(bounds.Width / NowPlayingLayout.DesignWidth, bounds.Height / NowPlayingLayout.DesignHeight);

        // Background panel tinted by artwork-derived color
        var artState = _artworkLoader?.Current;
        var bgColor = NowPlayingLayout.BlendToward(artState?.BackgroundColor ?? new SKColor(18, 18, 24), new SKColor(18, 18, 24), 0.25f);
        using var bg = new SKPaint { Color = bgColor, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 18f * scale, 18f * scale, bg);

        var snap = _mediaMonitor?.CurrentSnapshot;
        if (snap is null || snap.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed ||
            snap.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
        {
            DrawIdle(canvas, bounds, scale);
            return;
        }

        _layout = NowPlayingLayout.Compute(bounds, scale, ShowSourceBadge, MeasureBadgeTextWidth(snap, scale));
        EnsureIconPaths(_layout);

        DrawAlbumArt(canvas, bounds, scale);
        DrawSourceBadge(canvas, snap, scale);
        DrawTextInfo(canvas, bounds, snap, scale);
        DrawProgress(canvas, bounds, snap, scale);
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
        SKColor accent = ColorOf(AccentColorHex, new SKColor(255, 205, 133));

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, 64f * scale);
        using var iconPaint = new SKPaint { Color = accent.WithAlpha(200), IsAntialias = true };
        var tb = new SKRect();
        iconFont.MeasureText("🎵", out tb, iconPaint);
        canvas.DrawTextWithFallback("🎵", bounds.MidX - tb.MidX, bounds.MidY - 24f * scale, iconFont, iconPaint);

        var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 22f * scale);
        using var labelPaint = new SKPaint { Color = ColorOf(TextColorHex, SKColors.White).WithAlpha(180), IsAntialias = true };
        string hint = "No media playing — press play in any app";
        var lb = new SKRect();
        labelFont.MeasureText(hint, out lb, labelPaint);
        canvas.DrawTextWithFallback(hint, bounds.MidX - (lb.Width / 2f), bounds.MidY + 30f * scale, labelFont, labelPaint);
    }

    private void DrawSourceBadge(SKCanvas canvas, MediaSnapshot snap, float scale)
    {
        if (!_layout.SourceBadgeVisible) return;

        var pill = _layout.SourceBadgeRect;
        float h = pill.Height;
        float x = pill.Left;
        string name = NowPlayingPresentation.FriendlyAppName(snap.SourceAppId);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f * scale);

        using var pillBg = new SKPaint { Color = new SKColor(255, 255, 255, 25), IsAntialias = true };
        canvas.DrawRoundRect(pill, h / 2f, h / 2f, pillBg);

        using var pillBorder = new SKPaint { Color = new SKColor(255, 255, 255, 45), Style = SKPaintStyle.Stroke, StrokeWidth = 1f * scale, IsAntialias = true };
        canvas.DrawRoundRect(pill, h / 2f, h / 2f, pillBorder);

        using var dot = new SKPaint { Color = snap.IsPlaying ? new SKColor(34, 197, 94) : new SKColor(239, 68, 68), IsAntialias = true };
        canvas.DrawCircle(x + 11f * scale, pill.MidY, 3.5f * scale, dot);

        using var textPaint = new SKPaint { Color = ColorOf(TextColorHex, SKColors.White), IsAntialias = true };
        canvas.DrawTextWithFallback(name, x + 18f * scale, pill.MidY - font.Metrics.Top * 0.42f - 1f * scale, font, textPaint);
    }

    private void DrawAlbumArt(SKCanvas canvas, SKRect bounds, float scale)
    {
        float pad = 24f * scale;
        // Equal spacing pad from top, left, and bottom
        float artSide = _layout.ArtSide;
        float artTop = bounds.Top + pad + Math.Max(0f, (bounds.Height - pad * 2f - artSide) / 2f);
        var artRect = new SKRect(bounds.Left + pad, artTop,
                                 bounds.Left + pad + artSide, artTop + artSide);

        float r = 16f * scale;
        float shadowOff = 6f * scale;
        using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 110), IsAntialias = true };
        canvas.DrawRoundRect(new SKRect(artRect.Left + shadowOff, artRect.Top + shadowOff,
                                        artRect.Right + shadowOff, artRect.Bottom + shadowOff), r, r, shadow);

        // Snapshot the artwork once: background SMTC refreshes can replace (or
        // null) the published artwork between two reads, which would NRE the
        // render mid-draw. The retired-list discipline guarantees the snapshot
        // stays alive for this draw even if a refresh retires it right after.
        var art = _artworkLoader?.Current.Bitmap;
        if (art is not null)
        {
            canvas.Save();
            using (var clip = new SKPathBuilder())
            {
                clip.AddRoundRect(artRect, r, r);
                using var path = clip.Snapshot();
                canvas.ClipPath(path);
                canvas.DrawBitmap(art, artRect, HighQualitySampling);
            }
            canvas.Restore();
        }
        else
        {
            using var fill = new SKPaint { Color = ColorOf(AccentColorHex, new SKColor(255, 205, 133)).WithAlpha(80), IsAntialias = true };
            canvas.DrawRoundRect(artRect, r, r, fill);

            var font = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, artSide * 0.45f);
            using var iconPaint = new SKPaint { Color = SKColors.White.WithAlpha(220), IsAntialias = true };
            var tb = new SKRect();
            font.MeasureText("🎵", out tb, iconPaint);
            canvas.DrawTextWithFallback("🎵", artRect.MidX - tb.MidX, artRect.MidY - tb.MidY, font, iconPaint);
        }

        using var border = new SKPaint { Color = new SKColor(255, 255, 255, 45), Style = SKPaintStyle.Stroke, StrokeWidth = 1f * scale, IsAntialias = true };
        canvas.DrawRoundRect(artRect, r, r, border);
    }

    private void DrawTextInfo(SKCanvas canvas, SKRect bounds, MediaSnapshot snap, float scale)
    {
        float pad = 24f * scale;
        float artSide = _layout.ArtSide;
        float textX = bounds.Left + pad + artSide + 30f * scale;
        float textW = bounds.Right - pad - textX;
        if (textW <= 0) return;

        // Shift text stack down approx 3 lines total from top pad (2 lines lower than before)
        float textTop = bounds.Top + pad + Math.Max(0f, (artSide - 160f * scale) / 2f);
        SKColor text = ColorOf(TextColorHex, SKColors.White);
        SKColor accent = ColorOf(AccentColorHex, new SKColor(255, 205, 133));

        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 40f * scale);
        var artistFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 28f * scale);
        var albumFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 22f * scale);
        var metaFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 18f * scale);

        using var titlePaint = new SKPaint { Color = text, IsAntialias = true };
        using var artistPaint = new SKPaint { Color = text.WithAlpha(230), IsAntialias = true };
        using var albumPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
        using var metaPaint = new SKPaint { Color = accent, IsAntialias = true };

        float titleH = titleFont.Metrics.Bottom - titleFont.Metrics.Top;
        float artistH = artistFont.Metrics.Bottom - artistFont.Metrics.Top;
        float albumH = albumFont.Metrics.Bottom - albumFont.Metrics.Top;

        canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(IsEmpty(snap.Title) ? "Unknown Title" : snap.Title, titleFont, textW),
                        textX, textTop - titleFont.Metrics.Top, titleFont, titlePaint);

        float currentY = textTop + titleH + 6f * scale;

        if (!IsEmpty(snap.Artist))
        {
            canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(snap.Artist, artistFont, textW), textX, currentY - artistFont.Metrics.Top, artistFont, artistPaint);
            currentY += artistH + 5f * scale;
        }

        if (!IsEmpty(snap.Album))
        {
            canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(snap.Album, albumFont, textW), textX, currentY - albumFont.Metrics.Top, albumFont, albumPaint);
            currentY += albumH + 5f * scale;
        }

        string meta = NowPlayingPresentation.MetaLine(snap.TrackNumber, snap.AlbumTrackCount, snap.Genres);
        if (!string.IsNullOrEmpty(meta))
        {
            canvas.DrawTextWithFallback(meta, textX, currentY - metaFont.Metrics.Top, metaFont, metaPaint);
        }
    }

    private void DrawProgress(SKCanvas canvas, SKRect bounds, MediaSnapshot snap, float scale)
    {
        float pad = 24f * scale;
        float left = _layout.ProgressLeft;
        float right = bounds.Right - pad;
        float barY = _layout.ProgressY;
        float timeY = barY - 18f * scale;
        float barW = _layout.ProgressWidth;
        if (barW <= 0) return;

        double durSec = snap.Duration.TotalSeconds;
        double posSec = snap.Position.TotalSeconds;
        if (snap.IsPlaying)
            posSec += (Clock.GetUtcNow() - snap.LastUpdated).TotalSeconds;

        double ratio = NowPlayingPresentation.ProgressRatio(posSec, durSec);
        SKColor accent = ColorOf(AccentColorHex, new SKColor(255, 205, 133));

        // Time labels above progress bar track
        var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 16f * scale);
        using var timePaint = new SKPaint { Color = ColorOf(TextColorHex, SKColors.White).WithAlpha(210), IsAntialias = true };
        canvas.DrawTextWithFallback(NowPlayingPresentation.FormatTime(Math.Clamp(posSec, 0, Math.Max(0, durSec))), left, timeY, timeFont, timePaint);

        string durStr = NowPlayingPresentation.FormatTime(durSec);
        var db = new SKRect();
        timeFont.MeasureText(durStr, out db, timePaint);
        canvas.DrawTextWithFallback(durStr, right - db.Width, timeY, timeFont, timePaint);

        if (NowPlayingPresentation.PlaybackRateText(snap.PlaybackRate) is { } rate)
        {
            canvas.DrawTextWithFallback(rate, left + db.Width + 20f * scale, timeY, timeFont, timePaint);
        }

        // Background progress track
        using var bgPen = new SKPaint { Color = new SKColor(255, 255, 255, 35), StrokeWidth = 7f * scale, StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke };
        canvas.DrawLine(left, barY, right, barY, bgPen);

        if (ratio > 0)
        {
            using var fillPen = new SKPaint { Color = accent, StrokeWidth = 7f * scale, StrokeCap = SKStrokeCap.Round, IsAntialias = true, Style = SKPaintStyle.Stroke };
            canvas.DrawLine(left, barY, left + barW * (float)ratio, barY, fillPen);

            float dotR = 9f * scale;
            float dotX = left + barW * (float)ratio;
            using var dot = new SKPaint { Color = accent, IsAntialias = true };
            canvas.DrawCircle(dotX, barY, dotR, dot);
            using var dotCore = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawCircle(dotX, barY, 4f * scale, dotCore);
        }
    }

    private void DrawControls(SKCanvas canvas, MediaSnapshot snap, float scale)
    {
        SKColor text = ColorOf(TextColorHex, SKColors.White);
        SKColor accent = ColorOf(AccentColorHex, new SKColor(255, 205, 133));

        bool repeatActive = snap.Repeat != MediaPlaybackAutoRepeatMode.None;
        bool canPp = snap.IsPlaying ? snap.CanPause : snap.CanPlay;

        // One paint set per frame for all five controls; the icon geometry
        // itself is cached (EnsureIconPaths) and rebuilt only on resize.
        using var shufflePaint = new SKPaint { Color = IconColor(snap.Shuffle, snap.CanShuffle, accent, text), IsAntialias = true };
        using var prevPaint = new SKPaint { Color = IconColor(false, snap.CanPrev, accent, text), IsAntialias = true };
        using var nextPaint = new SKPaint { Color = IconColor(false, snap.CanNext, accent, text), IsAntialias = true };
        using var repeatPaint = new SKPaint { Color = IconColor(repeatActive, snap.CanRepeat, accent, text), IsAntialias = true };

        using var shuffleStroke = new SKPaint { Color = shufflePaint.Color, Style = SKPaintStyle.Stroke, StrokeWidth = _layout.ShuffleButton.Width * 0.07f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        using var repeatPen = new SKPaint { Color = repeatPaint.Color, Style = SKPaintStyle.Stroke, StrokeWidth = _layout.RepeatButton.Width * 0.07f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };

        using var heroBg = new SKPaint { Color = accent.WithAlpha(canPp ? (byte)245 : (byte)100), IsAntialias = true };
        using var heroGlow = new SKPaint { Color = accent.WithAlpha(canPp ? (byte)90 : (byte)20), Style = SKPaintStyle.Stroke, StrokeWidth = 2f * scale, IsAntialias = true };
        using var heroIcon = new SKPaint { Color = new SKColor(18, 18, 24), IsAntialias = true };

        // Shuffle (Clean icon button without glass circle)
        DrawShuffleIcon(canvas, shufflePaint, shuffleStroke);

        // Prev (Clean icon button without glass circle)
        DrawPrevIcon(canvas, _layout.PreviousButton, prevPaint);

        // Play / Pause (Hero Glowing Accent Button)
        DrawHeroPlayButton(canvas, _layout.PlayPauseButton, scale, heroBg, heroGlow, heroIcon, snap.IsPlaying);

        // Next (Clean icon button without glass circle)
        DrawNextIcon(canvas, _layout.NextButton, nextPaint);

        // Repeat (Clean icon button without glass circle)
        DrawRepeatIcon(canvas, _layout.RepeatButton, repeatPaint, repeatPen, snap.Repeat == MediaPlaybackAutoRepeatMode.Track);
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

    // ── Upgraded Vector Icon Drawing ──────────────────────────────────────────
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
            using var numPaint = new SKPaint { Color = paint.Color, IsAntialias = true };
            numFont.MeasureText("1", out var nb, numPaint);
            canvas.DrawTextWithFallback("1", cx - nb.Width / 2f, cy + nb.Height / 3f, numFont, numPaint);
        }
    }

    // ── Cached icon path management ────────────────────────────────────────

    /// <summary>
    /// Rebuilds the cached control-icon paths when the layout rects change
    /// (widget resize). The button rects are pure functions of the placement
    /// bounds and scale, so the shuffle rect alone keys the rebuild.
    /// </summary>
    private void EnsureIconPaths(NowPlayingGeometry layout)
    {
        if (_shuffleCurves is not null && SameRect(_iconPathKeyRect, layout.ShuffleButton))
        {
            return;
        }

        _iconPathKeyRect = layout.ShuffleButton;
        DisposeIconPaths();

        _shuffleCurves = BuildShuffleCurves(layout.ShuffleButton);
        _shuffleTopArrow = BuildShuffleArrow(layout.ShuffleButton, top: true);
        _shuffleBottomArrow = BuildShuffleArrow(layout.ShuffleButton, top: false);
        _prevTriangle = BuildPrevTriangle(layout.PreviousButton);
        _playTriangle = BuildPlayTriangle(layout.PlayPauseButton);
        _nextTriangle = BuildNextTriangle(layout.NextButton);
        _repeatArrow = BuildRepeatArrow(layout.RepeatButton);
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

    private static bool SameRect(SKRect a, SKRect b)
        => BitConverter.SingleToInt32Bits(a.Left) == BitConverter.SingleToInt32Bits(b.Left)
        && BitConverter.SingleToInt32Bits(a.Top) == BitConverter.SingleToInt32Bits(b.Top)
        && BitConverter.SingleToInt32Bits(a.Right) == BitConverter.SingleToInt32Bits(b.Right)
        && BitConverter.SingleToInt32Bits(a.Bottom) == BitConverter.SingleToInt32Bits(b.Bottom);

    private static SKPath BuildPrevTriangle(SKRect r)
    {
        float cx = r.MidX, cy = r.MidY;
        float h = r.Height * 0.32f;
        float barW = r.Width * 0.08f;
        float gap = r.Width * 0.06f;

        using var tri = new SKPathBuilder();
        tri.MoveTo(cx + r.Width * 0.20f, cy - h);
        tri.LineTo(cx - r.Width * 0.22f + barW + gap, cy);
        tri.LineTo(cx + r.Width * 0.20f, cy + h);
        tri.Close();
        return tri.Detach();
    }

    private static SKPath BuildPlayTriangle(SKRect r)
    {
        float cx = r.MidX + r.Width * 0.03f, cy = r.MidY;
        float h = r.Height * 0.32f;
        float w = r.Width * 0.28f;

        using var path = new SKPathBuilder();
        path.MoveTo(cx - w * 0.7f, cy - h);
        path.LineTo(cx + w, cy);
        path.LineTo(cx - w * 0.7f, cy + h);
        path.Close();
        return path.Detach();
    }

    private static SKPath BuildNextTriangle(SKRect r)
    {
        float cx = r.MidX, cy = r.MidY;
        float h = r.Height * 0.32f;
        float barW = r.Width * 0.08f;
        float gap = r.Width * 0.06f;

        using var tri = new SKPathBuilder();
        tri.MoveTo(cx - r.Width * 0.20f, cy - h);
        tri.LineTo(cx + r.Width * 0.22f - barW - gap, cy);
        tri.LineTo(cx - r.Width * 0.20f, cy + h);
        tri.Close();
        return tri.Detach();
    }

    private static SKPath BuildShuffleCurves(SKRect r)
    {
        float cx = r.MidX, cy = r.MidY;
        float w = r.Width * 0.20f;
        float h = r.Height * 0.20f;

        using var p = new SKPathBuilder();
        p.MoveTo(cx - w, cy - h);
        p.CubicTo(cx - w * 0.2f, cy - h, cx + w * 0.2f, cy + h, cx + w, cy + h);
        p.MoveTo(cx - w, cy + h);
        p.CubicTo(cx - w * 0.2f, cy + h, cx + w * 0.2f, cy - h, cx + w, cy - h);
        return p.Detach();
    }

    private static SKPath BuildShuffleArrow(SKRect r, bool top)
    {
        float cx = r.MidX, cy = r.MidY;
        float w = r.Width * 0.20f;
        float h = r.Height * 0.20f;
        float ah = r.Height * 0.12f;

        using var arr = new SKPathBuilder();
        if (top)
        {
            arr.MoveTo(cx + w, cy - h);
            arr.LineTo(cx + w - ah, cy - h - ah * 0.7f);
            arr.LineTo(cx + w - ah, cy - h + ah * 0.7f);
        }
        else
        {
            arr.MoveTo(cx + w, cy + h);
            arr.LineTo(cx + w - ah, cy + h - ah * 0.7f);
            arr.LineTo(cx + w - ah, cy + h + ah * 0.7f);
        }
        arr.Close();
        return arr.Detach();
    }

    private static SKPath BuildRepeatArrow(SKRect r)
    {
        float cx = r.MidX, cy = r.MidY;
        float outer = r.Width * 0.22f;
        float endDeg = 305f * MathF.PI / 180f;
        float tipX = cx + outer * MathF.Cos(endDeg);
        float tipY = cy + outer * MathF.Sin(endDeg);
        float tx = -MathF.Sin(endDeg);
        float ty = MathF.Cos(endDeg);
        float s = r.Width * 0.09f;

        using var tri = new SKPathBuilder();
        tri.MoveTo(tipX + tx * s, tipY + ty * s);
        tri.LineTo(tipX - tx * s * 0.35f - ty * s * 0.6f, tipY - ty * s * 0.35f + tx * s * 0.6f);
        tri.LineTo(tipX - tx * s * 0.35f + ty * s * 0.6f, tipY - ty * s * 0.35f - tx * s * 0.6f);
        tri.Close();
        return tri.Detach();
    }

    // ── Touch ─────────────────────────────────────────────────────────────

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchDown)
        {
            _touchDownPoint = localPoint;
            return;
        }

        if (eventType != TouchEventType.TouchUp) return;
        var snap = _mediaMonitor?.CurrentSnapshot;
        if (snap is null)
        {
            _touchDownPoint = null;
            return;
        }

        // Use the contact point rather than the release point so minor touch
        // movement does not turn a valid button press into a miss.
        SKPoint hitPoint = _touchDownPoint ?? localPoint;
        _touchDownPoint = null;

        switch (NowPlayingLayout.GetAction(_layout, hitPoint))
        {
            case NowPlayingHitAction.Shuffle when snap.CanShuffle:
                ToggleShuffle(snap);
                break;
            case NowPlayingHitAction.Previous when snap.CanPrev:
                _mediaMonitor?.Previous();
                break;
            case NowPlayingHitAction.PlayPause:
                TogglePlayPause(snap);
                break;
            case NowPlayingHitAction.Next when snap.CanNext:
                _mediaMonitor?.Next();
                break;
            case NowPlayingHitAction.Repeat when snap.CanRepeat:
                CycleRepeat(snap);
                break;
            case NowPlayingHitAction.SourceBadge:
                _mediaMonitor?.CycleSession();
                break;
            case NowPlayingHitAction.Seek when snap.CanSeek && snap.Duration.TotalSeconds > 0:
                SeekTo(hitPoint, snap);
                break;
        }
    }

    private void ToggleShuffle(MediaSnapshot snap) => _mediaMonitor?.SetShuffle(!snap.Shuffle);

    private void TogglePlayPause(MediaSnapshot snap)
    {
        if (snap.IsPlaying && snap.CanPause) _mediaMonitor?.Pause();
        else if (!snap.IsPlaying && snap.CanPlay) _mediaMonitor?.Play();
    }

    private void CycleRepeat(MediaSnapshot snap)
    {
        var next = snap.Repeat switch
        {
            MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            _ => MediaPlaybackAutoRepeatMode.None
        };
        _mediaMonitor?.SetRepeat(next);
    }

    private void SeekTo(SKPoint hitPoint, MediaSnapshot snap)
    {
        double ratio = NowPlayingPresentation.SeekRatio(hitPoint.X, _layout.ProgressLeft, _layout.ProgressWidth);
        _mediaMonitor?.Seek(TimeSpan.FromSeconds(ratio * snap.Duration.TotalSeconds));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static bool IsEmpty(string? s) => string.IsNullOrWhiteSpace(s);

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        DisposeIconPaths();

        if (_mediaMonitor is not null)
        {
            _mediaMonitor.SnapshotChanged -= OnMediaSnapshotChanged;
            await _mediaMonitor.DisposeAsync();
            _mediaMonitor = null;
        }
        if (_artworkLoader is not null)
        {
            _artworkLoader.ArtworkChanged -= OnArtworkChanged;
            _artworkLoader.DisposeAll();
            _artworkLoader = null;
        }

        await base.DisposeAsync();
    }
}
