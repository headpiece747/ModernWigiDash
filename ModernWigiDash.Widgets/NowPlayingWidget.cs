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
[WidgetMetadata("now_playing", "Now Playing", Description = "Displays live media playback (Spotify, browsers, VLC, iTunes, games) with album art, progress, shuffle/repeat, and touch controls via Windows media sessions.", Author = "ModernWigiDash", Version = "2.0.0", Category = "Media & Audio", DefaultGridSize = GridSizePreset.Size5x4)]
public sealed class NowPlayingWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size5x4.ToSize();
    public override SKSize MinimumSize => new SKSize(408, 150);

    private const float DesignWidth = 1016f;
    private const float DesignHeight = 592f;

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Progress fill, active toggles, and placeholder accent", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Title, artist, and icon color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Show Source Badge", WidgetPropertyType.Boolean, "Show which app is playing (tap to switch sources)", true)]
    public bool ShowSourceBadge { get; set; } = true;

    // ── SMTC state (all mutated on the UI thread) ─────────────────────────
    private MediaSessionMonitor? _mediaMonitor;
    private ArtworkLoader? _artworkLoader;
    private SKPoint? _touchDownPoint;
    private bool _disposed;

    // ── Hit rects populated during Render (used by OnTouch) ───────────────
    private SKRect _shuffleBtn, _prevBtn, _ppBtn, _nextBtn, _repeatBtn, _badgeBtn;
    private float _progressLeft, _progressWidth, _progressY;

    private static readonly SKSamplingOptions HighQualitySampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public override ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(context, cancellationToken);
        _artworkLoader = new ArtworkLoader(Context.LogError);
        _artworkLoader.ArtworkChanged += OnArtworkChanged;
        _mediaMonitor = new MediaSessionMonitor(Context.LogError);
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

        float scale = Math.Min(bounds.Width / DesignWidth, bounds.Height / DesignHeight);

        // Background panel tinted by artwork-derived color
        var artState = _artworkLoader?.Current;
        var bgColor = BlendToward(artState?.BackgroundColor ?? new SKColor(18, 18, 24), new SKColor(18, 18, 24), 0.25f);
        using var bg = new SKPaint { Color = bgColor, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 18f * scale, 18f * scale, bg);

        var snap = _mediaMonitor?.CurrentSnapshot;
        if (snap is null || snap.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed ||
            snap.Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
        {
            DrawIdle(canvas, bounds, scale);
            return;
        }

        DrawAlbumArt(canvas, bounds, scale);
        DrawSourceBadge(canvas, bounds, snap, scale);
        DrawTextInfo(canvas, bounds, snap, scale);
        DrawProgress(canvas, bounds, snap, scale);
        DrawControls(canvas, bounds, snap, scale);
    }

    private void DrawIdle(SKCanvas canvas, SKRect bounds, float scale)
    {
        SKColor accent = ParseColor(AccentColorHex, new SKColor(255, 205, 133));

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, 64f * scale);
        using var iconPaint = new SKPaint { Color = accent.WithAlpha(200), IsAntialias = true };
        var tb = new SKRect();
        iconFont.MeasureText("🎵", out tb, iconPaint);
        canvas.DrawTextWithFallback("🎵", bounds.MidX - tb.MidX, bounds.MidY - 24f * scale, iconFont, iconPaint);

        var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 22f * scale);
        using var labelPaint = new SKPaint { Color = ParseColor(TextColorHex, SKColors.White).WithAlpha(180), IsAntialias = true };
        string hint = "No media playing — press play in any app";
        var lb = new SKRect();
        labelFont.MeasureText(hint, out lb, labelPaint);
        canvas.DrawTextWithFallback(hint, bounds.MidX - (lb.Width / 2f), bounds.MidY + 30f * scale, labelFont, labelPaint);
    }

    private void DrawSourceBadge(SKCanvas canvas, SKRect bounds, MediaSnapshot snap, float scale)
    {
        if (!ShowSourceBadge) return;

        float pad = 24f * scale;
        string name = FriendlyAppName(snap.SourceAppId);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 14f * scale);
        using var textPaint = new SKPaint { Color = ParseColor(TextColorHex, SKColors.White), IsAntialias = true };
        float textW = FontHelper.MeasureTextWithFallback(name, font);
        float h = 26f * scale;
        float w = textW + 24f * scale;

        // Positioned right aligned at top-right of container
        float x = bounds.Right - pad - w;
        float y = bounds.Top + pad + 2f * scale;
        _badgeBtn = new SKRect(x, y, x + w, y + h);

        using var pillBg = new SKPaint { Color = new SKColor(255, 255, 255, 25), IsAntialias = true };
        canvas.DrawRoundRect(_badgeBtn, h / 2f, h / 2f, pillBg);

        using var pillBorder = new SKPaint { Color = new SKColor(255, 255, 255, 45), Style = SKPaintStyle.Stroke, StrokeWidth = 1f * scale, IsAntialias = true };
        canvas.DrawRoundRect(_badgeBtn, h / 2f, h / 2f, pillBorder);

        using var dot = new SKPaint { Color = snap.IsPlaying ? new SKColor(34, 197, 94) : new SKColor(239, 68, 68), IsAntialias = true };
        canvas.DrawCircle(x + 11f * scale, _badgeBtn.MidY, 3.5f * scale, dot);

        canvas.DrawTextWithFallback(name, x + 18f * scale, _badgeBtn.MidY - font.Metrics.Top * 0.42f - 1f * scale, font, textPaint);
    }

    private void DrawAlbumArt(SKCanvas canvas, SKRect bounds, float scale)
    {
        float pad = 24f * scale;
        // Equal spacing pad from top, left, and bottom
        float artSide = GetArtSide(bounds, scale);
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
            using var fill = new SKPaint { Color = ParseColor(AccentColorHex, new SKColor(255, 205, 133)).WithAlpha(80), IsAntialias = true };
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
        float artSide = GetArtSide(bounds, scale);
        float textX = bounds.Left + pad + artSide + 30f * scale;
        float textW = bounds.Right - pad - textX;
        if (textW <= 0) return;

        // Shift text stack down approx 3 lines total from top pad (2 lines lower than before)
        float textTop = bounds.Top + pad + Math.Max(0f, (artSide - 160f * scale) / 2f);
        SKColor text = ParseColor(TextColorHex, SKColors.White);
        SKColor accent = ParseColor(AccentColorHex, new SKColor(255, 205, 133));

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

        string meta = BuildMetaLine(snap);
        if (!string.IsNullOrEmpty(meta))
        {
            canvas.DrawTextWithFallback(meta, textX, currentY - metaFont.Metrics.Top, metaFont, metaPaint);
        }
    }

    private static string BuildMetaLine(MediaSnapshot snap)
    {
        List<string> parts = [];
        if (snap.TrackNumber > 0)
            parts.Add(snap.AlbumTrackCount > 0 ? $"Track {snap.TrackNumber}/{snap.AlbumTrackCount}" : $"Track {snap.TrackNumber}");
        if (snap.Genres.Length > 0)
            parts.Add(string.Join(" / ", snap.Genres.Take(2)));
        return string.Join(" · ", parts);
    }

    private void DrawProgress(SKCanvas canvas, SKRect bounds, MediaSnapshot snap, float scale)
    {
        float pad = 24f * scale;
        float artSide = GetArtSide(bounds, scale);
        float left = bounds.Left + pad + artSide + 30f * scale;
        float right = bounds.Right - pad;
        float barY = bounds.Bottom - pad - 92f * scale;
        float timeY = barY - 18f * scale;
        float barW = right - left;
        if (barW <= 0) return;

        _progressLeft = left;
        _progressWidth = barW;
        _progressY = barY;

        double durSec = snap.Duration.TotalSeconds;
        double posSec = snap.Position.TotalSeconds;
        if (snap.IsPlaying)
            posSec += (DateTimeOffset.Now - snap.LastUpdated).TotalSeconds;

        double ratio = durSec > 0 ? Math.Clamp(posSec / durSec, 0.0, 1.0) : 0.0;
        SKColor accent = ParseColor(AccentColorHex, new SKColor(255, 205, 133));

        // Time labels above progress bar track
        var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 16f * scale);
        using var timePaint = new SKPaint { Color = ParseColor(TextColorHex, SKColors.White).WithAlpha(210), IsAntialias = true };
        canvas.DrawTextWithFallback(FormatTime(Math.Clamp(posSec, 0, Math.Max(0, durSec))), left, timeY, timeFont, timePaint);

        string durStr = FormatTime(durSec);
        var db = new SKRect();
        timeFont.MeasureText(durStr, out db, timePaint);
        canvas.DrawTextWithFallback(durStr, right - db.Width, timeY, timeFont, timePaint);

        if (Math.Abs(snap.PlaybackRate - 1.0) > 0.001)
        {
            string rate = $"{snap.PlaybackRate:0.0}×";
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

    private void DrawControls(SKCanvas canvas, SKRect bounds, MediaSnapshot snap, float scale)
    {
        float pad = 24f * scale;
        float artSide = GetArtSide(bounds, scale);
        float areaLeft = bounds.Left + pad + artSide + 30f * scale;
        float areaW = bounds.Right - pad - areaLeft;
        float btnY = bounds.Bottom - pad - 32f * scale;
        float btnSize = 48f * scale;
        float ppSize = 58f * scale;
        float gap = 28f * scale;

        float totalW = btnSize * 4f + ppSize + gap * 4f;
        float startX = areaLeft + Math.Max(0, (areaW - totalW) / 2f);

        float shuffleX = startX;
        float prevX = shuffleX + btnSize + gap;
        float ppX = prevX + btnSize + gap;
        float nextX = ppX + ppSize + gap;
        float repeatX = nextX + btnSize + gap;

        _shuffleBtn = new SKRect(shuffleX, btnY - btnSize / 2f, shuffleX + btnSize, btnY + btnSize / 2f);
        _prevBtn = new SKRect(prevX, btnY - btnSize / 2f, prevX + btnSize, btnY + btnSize / 2f);
        _ppBtn = new SKRect(ppX, btnY - ppSize / 2f, ppX + ppSize, btnY + ppSize / 2f);
        _nextBtn = new SKRect(nextX, btnY - btnSize / 2f, nextX + btnSize, btnY + btnSize / 2f);
        _repeatBtn = new SKRect(repeatX, btnY - btnSize / 2f, repeatX + btnSize, btnY + btnSize / 2f);

        SKColor text = ParseColor(TextColorHex, SKColors.White);
        SKColor accent = ParseColor(AccentColorHex, new SKColor(255, 205, 133));

        // Shuffle (Clean icon button without glass circle)
        DrawCleanButton(canvas, _shuffleBtn, snap.CanShuffle, snap.Shuffle, accent, text, DrawShuffleIcon);

        // Prev (Clean icon button without glass circle)
        DrawCleanButton(canvas, _prevBtn, snap.CanPrev, false, accent, text, DrawPrevIcon);

        // Play / Pause (Hero Glowing Accent Button)
        bool canPp = snap.IsPlaying ? snap.CanPause : snap.CanPlay;
        DrawHeroPlayButton(canvas, _ppBtn, scale, canPp, snap.IsPlaying, accent);

        // Next (Clean icon button without glass circle)
        DrawCleanButton(canvas, _nextBtn, snap.CanNext, false, accent, text, DrawNextIcon);

        // Repeat (Clean icon button without glass circle)
        bool repeatActive = snap.Repeat != MediaPlaybackAutoRepeatMode.None;
        DrawCleanButton(canvas, _repeatBtn, snap.CanRepeat, repeatActive, accent, text,
            (c, r, p) => DrawRepeatIcon(c, r, p, snap.Repeat == MediaPlaybackAutoRepeatMode.Track));
    }

    private static float GetArtSide(SKRect bounds, float scale)
    {
        float pad = 24f * scale;
        float gap = 30f * scale;
        float controlRowWidth = (48f * 4f + 58f + 28f * 4f) * scale;
        float widthLimit = bounds.Width - pad * 2f - gap - controlRowWidth;
        return Math.Max(0f, Math.Min(bounds.Height - pad * 2f, widthLimit));
    }

    private static void DrawCleanButton(SKCanvas canvas, SKRect r, bool enabled, bool active, SKColor accent, SKColor text, Action<SKCanvas, SKRect, SKPaint> drawIcon)
    {
        SKColor iconColor;
        if (active) iconColor = accent;
        else iconColor = text.WithAlpha(enabled ? (byte)240 : (byte)70);
        using var iconPaint = new SKPaint { Color = iconColor, IsAntialias = true };
        drawIcon(canvas, r, iconPaint);
    }

    private static void DrawHeroPlayButton(SKCanvas canvas, SKRect r, float scale, bool enabled, bool isPlaying, SKColor accent)
    {
        // Hero Play button: Solid accent fill circular button
        using var btnBg = new SKPaint
        {
            Color = accent.WithAlpha(enabled ? (byte)245 : (byte)100),
            IsAntialias = true
        };
        canvas.DrawOval(r, btnBg);

        // Outer glow ring
        float glowOff = 4f * scale;
        var glowRect = new SKRect(r.Left - glowOff, r.Top - glowOff, r.Right + glowOff, r.Bottom + glowOff);
        using var glowBorder = new SKPaint
        {
            Color = accent.WithAlpha(enabled ? (byte)90 : (byte)20),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f * scale,
            IsAntialias = true
        };
        canvas.DrawOval(glowRect, glowBorder);

        // High contrast dark icon inside play button
        using var iconPaint = new SKPaint { Color = new SKColor(18, 18, 24), IsAntialias = true };
        if (isPlaying)
            DrawPauseIcon(canvas, r, iconPaint);
        else
            DrawPlayIcon(canvas, r, iconPaint);
    }

    // ── Upgraded Vector Icon Drawing ──────────────────────────────────────────

    private static void DrawPrevIcon(SKCanvas canvas, SKRect r, SKPaint paint)
    {
        float cx = r.MidX, cy = r.MidY;
        float h = r.Height * 0.32f;
        float barW = r.Width * 0.08f;
        float gap = r.Width * 0.06f;

        // Solid vertical bar
        var barRect = new SKRect(cx - r.Width * 0.22f, cy - h, cx - r.Width * 0.22f + barW, cy + h);
        canvas.DrawRoundRect(barRect, barW / 2f, barW / 2f, paint);

        // Smooth rounded triangle
        using var triPaint = new SKPaint
        {
            Color = paint.Color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        var tri = new SKPathBuilder();
        tri.MoveTo(cx + r.Width * 0.20f, cy - h);
        tri.LineTo(cx - r.Width * 0.22f + barW + gap, cy);
        tri.LineTo(cx + r.Width * 0.20f, cy + h);
        tri.Close();
        canvas.DrawPath(tri.Detach(), triPaint);
    }

    private static void DrawPlayIcon(SKCanvas canvas, SKRect r, SKPaint paint)
    {
        float cx = r.MidX + r.Width * 0.03f, cy = r.MidY;
        float h = r.Height * 0.32f;
        float w = r.Width * 0.28f;

        using var triPaint = new SKPaint
        {
            Color = paint.Color,
            Style = SKPaintStyle.Fill,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        var path = new SKPathBuilder();
        path.MoveTo(cx - w * 0.7f, cy - h);
        path.LineTo(cx + w, cy);
        path.LineTo(cx - w * 0.7f, cy + h);
        path.Close();
        canvas.DrawPath(path.Detach(), triPaint);
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

    private static void DrawNextIcon(SKCanvas canvas, SKRect r, SKPaint paint)
    {
        float cx = r.MidX, cy = r.MidY;
        float h = r.Height * 0.32f;
        float barW = r.Width * 0.08f;
        float gap = r.Width * 0.06f;

        // Smooth rounded triangle
        using var triPaint = new SKPaint
        {
            Color = paint.Color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        var tri = new SKPathBuilder();
        tri.MoveTo(cx - r.Width * 0.20f, cy - h);
        tri.LineTo(cx + r.Width * 0.22f - barW - gap, cy);
        tri.LineTo(cx - r.Width * 0.20f, cy + h);
        tri.Close();
        canvas.DrawPath(tri.Detach(), triPaint);

        // Solid vertical bar
        var barRect = new SKRect(cx + r.Width * 0.22f - barW, cy - h, cx + r.Width * 0.22f, cy + h);
        canvas.DrawRoundRect(barRect, barW / 2f, barW / 2f, paint);
    }

    private static void DrawShuffleIcon(SKCanvas canvas, SKRect r, SKPaint paint)
    {
        float cx = r.MidX, cy = r.MidY;
        float w = r.Width * 0.20f;
        float h = r.Height * 0.20f;
        float ah = r.Height * 0.12f;

        using var stroke = new SKPaint
        {
            Color = paint.Color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = r.Width * 0.07f,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };

        var p = new SKPathBuilder();
        p.MoveTo(cx - w, cy - h);
        p.CubicTo(cx - w * 0.2f, cy - h, cx + w * 0.2f, cy + h, cx + w, cy + h);
        p.MoveTo(cx - w, cy + h);
        p.CubicTo(cx - w * 0.2f, cy + h, cx + w * 0.2f, cy - h, cx + w, cy - h);
        canvas.DrawPath(p.Detach(), stroke);

        // Arrowheads
        var arrTop = new SKPathBuilder();
        arrTop.MoveTo(cx + w, cy - h);
        arrTop.LineTo(cx + w - ah, cy - h - ah * 0.7f);
        arrTop.LineTo(cx + w - ah, cy - h + ah * 0.7f);
        arrTop.Close();
        canvas.DrawPath(arrTop.Detach(), paint);

        var arrBot = new SKPathBuilder();
        arrBot.MoveTo(cx + w, cy + h);
        arrBot.LineTo(cx + w - ah, cy + h - ah * 0.7f);
        arrBot.LineTo(cx + w - ah, cy + h + ah * 0.7f);
        arrBot.Close();
        canvas.DrawPath(arrBot.Detach(), paint);
    }

    private static void DrawRepeatIcon(SKCanvas canvas, SKRect r, SKPaint paint, bool repeatOne)
    {
        float cx = r.MidX, cy = r.MidY;
        float outer = r.Width * 0.22f;
        float strokeW = r.Width * 0.07f;

        var oval = new SKRect(cx - outer, cy - outer, cx + outer, cy + outer);
        using var pen = new SKPaint
        {
            Color = paint.Color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeW,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };
        canvas.DrawArc(oval, 55f, 250f, false, pen);

        // Arrowhead
        float endDeg = 305f * MathF.PI / 180f;
        float tipX = cx + outer * MathF.Cos(endDeg);
        float tipY = cy + outer * MathF.Sin(endDeg);
        float tx = -MathF.Sin(endDeg);
        float ty = MathF.Cos(endDeg);
        float s = r.Width * 0.09f;

        var tri = new SKPathBuilder();
        tri.MoveTo(tipX + tx * s, tipY + ty * s);
        tri.LineTo(tipX - tx * s * 0.35f - ty * s * 0.6f, tipY - ty * s * 0.35f + tx * s * 0.6f);
        tri.LineTo(tipX - tx * s * 0.35f + ty * s * 0.6f, tipY - ty * s * 0.35f - tx * s * 0.6f);
        tri.Close();
        canvas.DrawPath(tri.Detach(), paint);

        if (repeatOne)
        {
            var numFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, r.Width * 0.24f);
            using var numPaint = new SKPaint { Color = paint.Color, IsAntialias = true };
            numFont.MeasureText("1", out var nb, numPaint);
            canvas.DrawTextWithFallback("1", cx - nb.Width / 2f, cy + nb.Height / 3f, numFont, numPaint);
        }
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

        if (_shuffleBtn.Contains(hitPoint) && snap.CanShuffle)
        {
            _mediaMonitor?.SetShuffle(!snap.Shuffle);
        }
        else if (_prevBtn.Contains(hitPoint) && snap.CanPrev)
        {
            _mediaMonitor?.Previous();
        }
        else if (_ppBtn.Contains(hitPoint))
        {
            if (snap.IsPlaying && snap.CanPause) _mediaMonitor?.Pause();
            else if (!snap.IsPlaying && snap.CanPlay) _mediaMonitor?.Play();
        }
        else if (_nextBtn.Contains(hitPoint) && snap.CanNext)
        {
            _mediaMonitor?.Next();
        }
        else if (_repeatBtn.Contains(hitPoint) && snap.CanRepeat)
        {
            var next = snap.Repeat switch
            {
                MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                _ => MediaPlaybackAutoRepeatMode.None
            };
            _mediaMonitor?.SetRepeat(next);
        }
        else if (_badgeBtn.Contains(hitPoint))
        {
            _mediaMonitor?.CycleSession();
        }
        else if (_progressWidth > 0 && snap.Duration.TotalSeconds > 0
                  && Math.Abs(hitPoint.Y - _progressY) <= 24f
                  && hitPoint.X >= _progressLeft
                  && hitPoint.X <= _progressLeft + _progressWidth
                 && snap.CanSeek)
        {
            double ratio = Math.Clamp((hitPoint.X - _progressLeft) / _progressWidth, 0.0, 1.0);
            _mediaMonitor?.Seek(TimeSpan.FromSeconds(ratio * snap.Duration.TotalSeconds));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static SKColor BlendToward(SKColor from, SKColor to, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new SKColor(
            (byte)(from.Red + (to.Red - from.Red) * amount),
            (byte)(from.Green + (to.Green - from.Green) * amount),
            (byte)(from.Blue + (to.Blue - from.Blue) * amount),
            from.Alpha);
    }

    private SKColor ParseColor(string hex, SKColor fallback)
        => ColorOf(hex, fallback);

    private static string FriendlyAppName(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return "Media";
        string lower = appId.ToLowerInvariant();

        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("chrome")) return "Chrome";
        if (lower.Contains("msedge")) return "Edge";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("vlc")) return "VLC";
        if (lower.Contains("itunes")) return "iTunes";
        if (lower.Contains("apple") || lower.Contains("music")) return "Apple Music";
        if (lower.Contains("mediaplayer") || lower.Contains("wmplayer")) return "Windows Media Player";
        if (lower.Contains("discord")) return "Discord";
        if (lower.Contains("foobar")) return "foobar2000";
        if (lower.Contains("steam")) return "Steam";

        int slash = appId.LastIndexOf('!');
        string name = slash >= 0 ? appId[(slash + 1)..] : appId;
        int dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        return name.Length > 16 ? name[..16] : name;
    }

    private static bool IsEmpty(string? s) => string.IsNullOrWhiteSpace(s);

    private static string FormatTime(double totalSeconds)
    {
        if (totalSeconds < 0 || double.IsNaN(totalSeconds) || double.IsInfinity(totalSeconds)) return "0:00";
        var ts = TimeSpan.FromSeconds(totalSeconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

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
