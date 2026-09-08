namespace ModernWigiDash.Widgets;

/// <summary>
/// Draws the Now Playing widget's scene: the idle panel, or the active layout
/// (album art, source badge, title/artist meta, progress band, and the
/// control row) from the per-frame geometry record. The widget keeps only
/// orchestration - the monitor/artwork lifecycle, the touch routing, and the
/// frame inputs - so every paint decision lives here, behind one interface
/// the tests drive without a widget instance (the WeatherWidgetRenderer
/// precedent). All paints are hoisted: colors mutate per render
/// (property/snapshot-driven), so the 30 FPS render allocates no SKPaint.
/// </summary>
internal sealed class NowPlayingRenderer : IDisposable
{
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

    /// <summary>Releases the hoisted paints' and cached paths' native Skia handles - the owning widget's DisposeAsync calls this.</summary>
    public void Dispose()
    {
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
    }

    /// <summary>The idle panel: the music glyph and the "no media playing" hint, centered in the bounds.</summary>
    internal void DrawIdle(SKCanvas canvas, SKRect bounds, float scale, SKColor accent, SKColor text)
    {
        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, 64f * scale);
        _idleIconPaint.Color = accent.WithAlpha(200);
        var tb = new SKRect();
        iconFont.MeasureText("\U0001F3B5", out tb, _idleIconPaint);
        canvas.DrawTextWithFallback("\U0001F3B5", bounds.MidX - tb.MidX, bounds.MidY - 24f * scale, iconFont, _idleIconPaint);

        var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 22f * scale);
        _idleLabelPaint.Color = text.WithAlpha(180);
        string hint = "No media playing — press play in any app";
        var lb = new SKRect();
        labelFont.MeasureText(hint, out lb, _idleLabelPaint);
        canvas.DrawTextWithFallback(hint, bounds.MidX - (lb.Width / 2f), bounds.MidY + 30f * scale, labelFont, _idleLabelPaint);
    }

    /// <summary>The background panel tinted toward the artwork-derived color.</summary>
    internal void DrawBackground(SKCanvas canvas, SKRect bounds, float scale, SKColor? artworkBackgroundColor)
    {
        var bgColor = NowPlayingLayout.BlendToward(artworkBackgroundColor ?? new SKColor(18, 18, 24), new SKColor(18, 18, 24), 0.25f);
        _bgPaint.Color = bgColor;
        canvas.DrawRoundRect(bounds, 18f * scale, 18f * scale, _bgPaint);
    }

    /// <summary>The full active scene: album art, source badge, text info, progress, controls. <paramref name="layout"/> is the frame's geometry record; <paramref name="clockNow"/> feeds the position extrapolation.</summary>
    internal void DrawActiveScene(
        SKCanvas canvas,
        SKRect bounds,
        float scale,
        NowPlayingGeometry layout,
        MediaSnapshot snap,
        SKBitmap? artwork,
        SKColor accent,
        SKColor text,
        DateTimeOffset clockNow)
    {
        EnsureIconPaths(layout);

        DrawAlbumArt(canvas, bounds, scale, layout, artwork, accent);
        DrawSourceBadge(canvas, snap, scale, layout, text);
        DrawTextInfo(canvas, bounds, snap, scale, layout, text, accent);
        DrawProgress(canvas, snap, scale, layout, accent, text, clockNow);
        DrawControls(canvas, snap, scale, layout, accent, text);
    }

    private void DrawSourceBadge(SKCanvas canvas, MediaSnapshot snap, float scale, NowPlayingGeometry layout, SKColor text)
    {
        if (!layout.SourceBadgeVisible) return;

        var pill = layout.SourceBadgeRect;
        float h = pill.Height;
        float x = pill.Left;
        string name = NowPlayingPresentation.FriendlyAppName(snap.SourceAppId);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f * scale);

        canvas.DrawRoundRect(pill, h / 2f, h / 2f, _pillBgPaint);

        _pillBorderPaint.StrokeWidth = 1f * scale;
        canvas.DrawRoundRect(pill, h / 2f, h / 2f, _pillBorderPaint);

        _statusDotPaint.Color = snap.IsPlaying ? new SKColor(34, 197, 94) : new SKColor(239, 68, 68);
        canvas.DrawCircle(x + 11f * scale, pill.MidY, 3.5f * scale, _statusDotPaint);

        _badgeTextPaint.Color = text;
        canvas.DrawTextWithFallback(name, x + 18f * scale, pill.MidY - font.Metrics.Top * 0.42f - 1f * scale, font, _badgeTextPaint);
    }

    private void DrawAlbumArt(SKCanvas canvas, SKRect bounds, float scale, NowPlayingGeometry layout, SKBitmap? artwork, SKColor accent)
    {
        float pad = layout.Pad;
        // Equal spacing pad from top, left, and bottom
        float artSide = layout.ArtSide;
        float artTop = bounds.Top + pad + Math.Max(0f, (bounds.Height - pad * 2f - artSide) / 2f);
        var artRect = new SKRect(bounds.Left + pad, artTop,
                                 bounds.Left + pad + artSide, artTop + artSide);

        float r = 16f * scale;
        float shadowOff = 6f * scale;
        canvas.DrawRoundRect(new SKRect(artRect.Left + shadowOff, artRect.Top + shadowOff,
                                        artRect.Right + shadowOff, artRect.Bottom + shadowOff), r, r, _shadowPaint);

        // The artwork snapshot is taken by the caller (the widget) before this
        // call: background SMTC refreshes can replace (or null) the published
        // artwork between two reads, which would NRE the render mid-draw. The
        // retired-list discipline guarantees the snapshot stays alive for this
        // draw even if a refresh retires it right after.
        if (artwork is not null)
        {
            canvas.Save();
            EnsureAlbumClipPath(artRect, r);
            canvas.ClipPath(_albumClipPath);
            canvas.DrawBitmap(artwork, artRect, HighQualitySampling);
            canvas.Restore();
        }
        else
        {
            _artFillPaint.Color = accent.WithAlpha(80);
            canvas.DrawRoundRect(artRect, r, r, _artFillPaint);

            var font = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, artSide * 0.45f);
            _artIconPaint.Color = SKColors.White.WithAlpha(220);
            var tb = new SKRect();
            font.MeasureText("\U0001F3B5", out tb, _artIconPaint);
            canvas.DrawTextWithFallback("\U0001F3B5", artRect.MidX - tb.MidX, artRect.MidY - tb.MidY, font, _artIconPaint);
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

    private void DrawTextInfo(SKCanvas canvas, SKRect bounds, MediaSnapshot snap, float scale, NowPlayingGeometry layout, SKColor text, SKColor accent)
    {
        float pad = layout.Pad;
        float artSide = layout.ArtSide;
        // The text column shares the progress band's left edge — one column,
        // one X, from the layout record (the layout's barW is right - left).
        float textX = layout.ProgressLeft;
        float textW = bounds.Right - pad - textX;
        if (textW <= 0) return;

        // Shift text stack down approx 3 lines total from top pad (2 lines lower than before)
        float textTop = bounds.Top + pad + Math.Max(0f, (artSide - 160f * scale) / 2f);

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

    private void DrawProgress(SKCanvas canvas, MediaSnapshot snap, float scale, NowPlayingGeometry layout, SKColor accent, SKColor text, DateTimeOffset clockNow)
    {
        float left = layout.ProgressLeft;
        float barY = layout.ProgressY;
        float timeY = barY - 18f * scale;
        float barW = layout.ProgressWidth;
        if (barW <= 0) return;
        // The column's right edge is one fact: the band spans exactly
        // left..left+barW (the layout computed barW as right - left).
        float right = left + barW;

        double durSec = snap.Duration.TotalSeconds;
        double posSec = NowPlayingPresentation.ExtrapolatedPosition(snap, clockNow);

        double ratio = NowPlayingPresentation.ProgressRatio(posSec, durSec);

        // Time labels above progress bar track
        var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 16f * scale);
        _timePaint.Color = text.WithAlpha(210);
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

    private void DrawControls(SKCanvas canvas, MediaSnapshot snap, float scale, NowPlayingGeometry layout, SKColor accent, SKColor text)
    {
        bool repeatActive = snap.Repeat != MediaRepeatMode.None;
        bool canPp = snap.IsPlaying ? snap.CanPause : snap.CanPlay;

        // One paint set per frame for all five controls; the icon geometry
        // itself is cached (EnsureIconPaths) and rebuilt only on resize.
        _shufflePaint.Color = IconColor(snap.Shuffle, snap.CanShuffle, accent, text);
        _prevPaint.Color = IconColor(false, snap.CanPrev, accent, text);
        _nextPaint.Color = IconColor(false, snap.CanNext, accent, text);
        _repeatPaint.Color = IconColor(repeatActive, snap.CanRepeat, accent, text);

        _shuffleStrokePaint.Color = _shufflePaint.Color;
        _shuffleStrokePaint.StrokeWidth = layout.ShuffleButton.Width * 0.07f;
        _repeatPenPaint.Color = _repeatPaint.Color;
        _repeatPenPaint.StrokeWidth = layout.RepeatButton.Width * 0.07f;

        _heroBgPaint.Color = accent.WithAlpha(canPp ? (byte)245 : (byte)100);
        _heroGlowPaint.Color = accent.WithAlpha(canPp ? (byte)90 : (byte)20);
        _heroGlowPaint.StrokeWidth = 2f * scale;

        // Shuffle (Clean icon button without glass circle)
        DrawShuffleIcon(canvas, _shufflePaint, _shuffleStrokePaint);

        // Prev (Clean icon button without glass circle)
        DrawPrevIcon(canvas, layout.PreviousButton, _prevPaint);

        // Play / Pause (Hero Glowing Accent Button)
        DrawHeroPlayButton(canvas, layout.PlayPauseButton, scale, _heroBgPaint, _heroGlowPaint, _heroIconPaint, snap.IsPlaying);

        // Next (Clean icon button without glass circle)
        DrawNextIcon(canvas, layout.NextButton, _nextPaint);

        // Repeat (Clean icon button without glass circle)
        DrawRepeatIcon(canvas, layout.RepeatButton, _repeatPaint, _repeatPenPaint, snap.Repeat == MediaRepeatMode.Track);
    }

    /// <summary>The control icon's color: the accent when active, the text color at reduced alpha otherwise (dimmed when the session reports the capability off).</summary>
    internal static SKColor IconColor(bool active, bool enabled, SKColor accent, SKColor text)
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

    private static bool IsEmpty(string? s) => string.IsNullOrWhiteSpace(s);
}
