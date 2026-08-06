using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
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
[WidgetMetadata(
    "now_playing",
    "Now Playing",
    "Displays live media playback (Spotify, browsers, VLC, iTunes, games) with album art, progress, shuffle/repeat, and touch controls via Windows media sessions.",
    "ModernWigiDash",
    "2.0.0",
    "Media & Audio",
    GridSizePreset.Size5x4)]
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
    private GlobalSystemMediaTransportControlsSessionManager? _smctManager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaSnapshot? _snapshot;
    private SKBitmap? _albumArt;
    private string _artKey = "";
    private string _loadedArtworkKey = "";
    private string _loadingArtworkKey = "";
    private SKColor _bgColor = new(18, 18, 24);
    private int _artLoadVersion;
    private int _refreshVersion;
    private SKPoint? _touchDownPoint;
    private bool _disposed;

    // ── Hit rects populated during Render (used by OnTouch) ───────────────
    private SKRect _shuffleBtn, _prevBtn, _ppBtn, _nextBtn, _repeatBtn, _badgeBtn;
    private float _progressLeft, _progressWidth, _progressY;

    private static readonly SKSamplingOptions HighQualitySampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private sealed class MediaSnapshot
    {
        public string SourceAppId = "";
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string AlbumArtist = "";
        public int TrackNumber;
        public int AlbumTrackCount;
        public string[] Genres = System.Array.Empty<string>();
        public GlobalSystemMediaTransportControlsSessionPlaybackStatus Status;
        public TimeSpan Position;
        public TimeSpan Duration;
        public DateTimeOffset LastUpdated;
        public bool Shuffle;
        public MediaPlaybackAutoRepeatMode Repeat;
        public double PlaybackRate = 1.0;
        public bool CanPlay, CanPause, CanStop, CanNext, CanPrev, CanSeek, CanShuffle, CanRepeat;
        public bool IsPlaying => Status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public override ValueTask InitializeAsync(ModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(context, cancellationToken);
        _ = InitSmctAsync();
        return ValueTask.CompletedTask;
    }

    private async Task InitSmctAsync()
    {
        try
        {
            // Runs synchronously up to the first await on the WPF UI thread (STA),
            // so the manager is created in the interactive session's apartment.
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed) return;

            _smctManager = manager;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            manager.SessionsChanged += OnSessionsChanged;
            AttachSession(manager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            Context?.LogError($"SMTC init failed: {ex.Message}", ex);
            await PushRenderAsync();
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
    {
        AttachSession(sender.GetCurrentSession());
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
    {
        if (_session is null)
            AttachSession(sender.GetCurrentSession());
        else
            _ = RefreshAsync();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshAsync();

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_session, session)) return;

        DetachSessionEvents();
        _session = session;

        if (session != null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        }

        _ = RefreshAsync();
    }

    private void DetachSessionEvents()
    {
        if (_session == null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
    }

    private async Task RefreshAsync()
    {
        int refreshVersion = ++_refreshVersion;
        var session = _session;
        if (session is null)
        {
            _snapshot = null;
            DisposeArtwork();
            _bgColor = new SKColor(18, 18, 24);
            await PushRenderAsync();
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            if (_disposed || refreshVersion != _refreshVersion) return;

            var previous = _snapshot;
            _snapshot = new MediaSnapshot
            {
                SourceAppId = session.SourceAppUserModelId ?? "",
                Title = Sanitize(props?.Title, ""),
                Artist = Sanitize(props?.Artist, ""),
                Album = Sanitize(props?.AlbumTitle, ""),
                AlbumArtist = Sanitize(props?.AlbumArtist, ""),
                TrackNumber = props?.TrackNumber ?? 0,
                AlbumTrackCount = props?.AlbumTrackCount ?? 0,
                Genres = props?.Genres?.ToArray() ?? System.Array.Empty<string>(),
                Status = info?.PlaybackStatus ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed,
                Position = timeline?.Position ?? TimeSpan.Zero,
                Duration = timeline?.EndTime ?? TimeSpan.Zero,
                LastUpdated = timeline?.LastUpdatedTime ?? DateTimeOffset.Now,
                Shuffle = info?.IsShuffleActive ?? false,
                Repeat = info?.AutoRepeatMode ?? MediaPlaybackAutoRepeatMode.None,
                PlaybackRate = info?.PlaybackRate is > 0 ? info.PlaybackRate.Value : 1.0,
                CanPlay = info?.Controls.IsPlayEnabled ?? false,
                CanPause = info?.Controls.IsPauseEnabled ?? false,
                CanStop = info?.Controls.IsStopEnabled ?? false,
                CanNext = info?.Controls.IsNextEnabled ?? false,
                CanPrev = info?.Controls.IsPreviousEnabled ?? false,
                CanSeek = info?.Controls.IsPlaybackPositionEnabled ?? false,
                CanShuffle = info?.Controls.IsShuffleEnabled ?? false,
                CanRepeat = info?.Controls.IsRepeatEnabled ?? false
            };

            string artKey = $"{session.SourceAppUserModelId}:{props?.Title}:{props?.Artist}:{props?.AlbumTitle}";
            if (previous is not null && previous.Position > TimeSpan.FromSeconds(3) &&
                _snapshot.Position < TimeSpan.FromSeconds(2.5))
            {
                // Some SMTC providers reuse metadata across track transitions.
                // A timeline reset is still a reliable signal to reload artwork.
                artKey += $":track{refreshVersion}";
            }

            bool trackChanged = artKey != _artKey;
            bool artworkBecameAvailable = props?.Thumbnail is not null &&
                _loadedArtworkKey != artKey && _loadingArtworkKey != artKey;
            if (trackChanged || artworkBecameAvailable)
            {
                _artKey = artKey;
                await LoadArtworkAsync(props?.Thumbnail, artKey);
            }

            await PushRenderAsync();
        }
        catch (Exception ex)
        {
            Context?.LogError($"SMTC refresh failed: {ex.Message}", ex);
        }
    }

    private async Task LoadArtworkAsync(IRandomAccessStreamReference? thumbnail, string artKey)
    {
        int version = ++_artLoadVersion;
        DisposeArtwork();

        if (thumbnail is null)
        {
            _bgColor = new SKColor(18, 18, 24);
            return;
        }

        _loadingArtworkKey = artKey;
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (_disposed || version != _artLoadVersion || artKey != _artKey) return;

            ulong size = stream.Size;
            if (size == 0 || size > 10UL * 1024 * 1024)
            {
                _bgColor = new SKColor(18, 18, 24);
                return;
            }

            byte[] data = new byte[(int)size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)size);
                reader.ReadBytes(data);
            }

            if (_disposed || version != _artLoadVersion || artKey != _artKey) return;

            var decoded = await Task.Run(() => SKBitmap.Decode(data));
            if (_disposed || version != _artLoadVersion || artKey != _artKey)
            {
                decoded?.Dispose();
                return;
            }

            _albumArt = decoded;
            ExtractBackgroundColor();
            _loadedArtworkKey = artKey;
        }
        catch (Exception ex)
        {
            Context?.LogError($"Album art decode failed: {ex.Message}", ex);
            _bgColor = new SKColor(18, 18, 24);
        }
        finally
        {
            if (_loadingArtworkKey == artKey)
                _loadingArtworkKey = "";
        }
    }

    private void DisposeArtwork()
    {
        _albumArt?.Dispose();
        _albumArt = null;
        _loadedArtworkKey = "";
    }

    private async Task PushRenderAsync()
    {
        Context?.RequestRender();
        await Task.CompletedTask;
    }

    // ── Render ────────────────────────────────────────────────────────────

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        float scale = Math.Min(bounds.Width / DesignWidth, bounds.Height / DesignHeight);

        // Background panel tinted by artwork-derived color
        var bgColor = BlendToward(_bgColor, new SKColor(18, 18, 24), 0.25f);
        using var bg = new SKPaint { Color = bgColor, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 18f * scale, 18f * scale, bg);

        var snap = _snapshot;
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

        using var iconFont = FontHelper.CreateFont("Segoe UI Emoji", SKFontStyle.Bold, 64f * scale);
        using var iconPaint = new SKPaint { Color = accent.WithAlpha(200), IsAntialias = true };
        var tb = new SKRect();
        iconFont.MeasureText("🎵", out tb, iconPaint);
        canvas.DrawText("🎵", bounds.MidX - tb.MidX, bounds.MidY - 24f * scale, SKTextAlign.Left, iconFont, iconPaint);

        using var labelFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, 22f * scale);
        using var labelPaint = new SKPaint { Color = ParseColor(TextColorHex, SKColors.White).WithAlpha(180), IsAntialias = true };
        string hint = "No media playing — press play in any app";
        var lb = new SKRect();
        labelFont.MeasureText(hint, out lb, labelPaint);
        canvas.DrawText(hint, bounds.MidX - (lb.Width / 2f), bounds.MidY + 30f * scale, SKTextAlign.Left, labelFont, labelPaint);
    }

    private void DrawSourceBadge(SKCanvas canvas, SKRect bounds, MediaSnapshot snap, float scale)
    {
        if (!ShowSourceBadge) return;

        float pad = 24f * scale;
        string name = FriendlyAppName(snap.SourceAppId);
        using var font = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), 14f * scale);
        FontHelper.ConfigureHighQualityFont(font);
        using var textPaint = new SKPaint { Color = ParseColor(TextColorHex, SKColors.White), IsAntialias = true };
        float textW = font.MeasureText(name);
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

        canvas.DrawText(name, x + 18f * scale, _badgeBtn.MidY - font.Metrics.Top * 0.42f - 1f * scale, SKTextAlign.Left, font, textPaint);
    }

    private SKRect DrawAlbumArt(SKCanvas canvas, SKRect bounds, float scale)
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

        if (_albumArt is not null)
        {
            canvas.Save();
            using (var clip = new SKPathBuilder())
            {
                clip.AddRoundRect(artRect, r, r);
                using var path = clip.Snapshot();
                canvas.ClipPath(path);
                canvas.DrawBitmap(_albumArt, artRect, HighQualitySampling);
            }
            canvas.Restore();
        }
        else
        {
            using var fill = new SKPaint { Color = ParseColor(AccentColorHex, new SKColor(255, 205, 133)).WithAlpha(80), IsAntialias = true };
            canvas.DrawRoundRect(artRect, r, r, fill);

            using var font = FontHelper.CreateFont("Segoe UI Emoji", SKFontStyle.Bold, artSide * 0.45f);
            using var iconPaint = new SKPaint { Color = SKColors.White.WithAlpha(220), IsAntialias = true };
            var tb = new SKRect();
            font.MeasureText("🎵", out tb, iconPaint);
            canvas.DrawText("🎵", artRect.MidX - tb.MidX, artRect.MidY - tb.MidY, SKTextAlign.Left, font, iconPaint);
        }

        using var border = new SKPaint { Color = new SKColor(255, 255, 255, 45), Style = SKPaintStyle.Stroke, StrokeWidth = 1f * scale, IsAntialias = true };
        canvas.DrawRoundRect(artRect, r, r, border);
        return artRect;
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

        using var titleFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), 40f * scale);
        FontHelper.ConfigureHighQualityFont(titleFont);
        using var artistFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), 28f * scale);
        FontHelper.ConfigureHighQualityFont(artistFont);
        using var albumFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Normal), 22f * scale);
        FontHelper.ConfigureHighQualityFont(albumFont);
        using var metaFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Normal), 18f * scale);
        FontHelper.ConfigureHighQualityFont(metaFont);

        using var titlePaint = new SKPaint { Color = text, IsAntialias = true };
        using var artistPaint = new SKPaint { Color = text.WithAlpha(230), IsAntialias = true };
        using var albumPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
        using var metaPaint = new SKPaint { Color = accent, IsAntialias = true };

        float titleH = titleFont.Metrics.Bottom - titleFont.Metrics.Top;
        float artistH = artistFont.Metrics.Bottom - artistFont.Metrics.Top;
        float albumH = albumFont.Metrics.Bottom - albumFont.Metrics.Top;

        canvas.DrawText(TextRenderHelper.TruncateText(IsEmpty(snap.Title) ? "Unknown Title" : snap.Title, titleFont, textW),
                        textX, textTop - titleFont.Metrics.Top, SKTextAlign.Left, titleFont, titlePaint);

        float currentY = textTop + titleH + 6f * scale;

        if (!IsEmpty(snap.Artist))
        {
            canvas.DrawText(TextRenderHelper.TruncateText(snap.Artist, artistFont, textW), textX, currentY - artistFont.Metrics.Top, SKTextAlign.Left, artistFont, artistPaint);
            currentY += artistH + 5f * scale;
        }

        if (!IsEmpty(snap.Album))
        {
            canvas.DrawText(TextRenderHelper.TruncateText(snap.Album, albumFont, textW), textX, currentY - albumFont.Metrics.Top, SKTextAlign.Left, albumFont, albumPaint);
            currentY += albumH + 5f * scale;
        }

        string meta = BuildMetaLine(snap);
        if (!string.IsNullOrEmpty(meta))
        {
            canvas.DrawText(meta, textX, currentY - metaFont.Metrics.Top, SKTextAlign.Left, metaFont, metaPaint);
        }
    }

    private static string BuildMetaLine(MediaSnapshot snap)
    {
        var parts = new List<string>();
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
        using var timeFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), 16f * scale);
        FontHelper.ConfigureHighQualityFont(timeFont);
        using var timePaint = new SKPaint { Color = ParseColor(TextColorHex, SKColors.White).WithAlpha(210), IsAntialias = true };
        canvas.DrawText(FormatTime(Math.Clamp(posSec, 0, Math.Max(0, durSec))), left, timeY, SKTextAlign.Left, timeFont, timePaint);

        string durStr = FormatTime(durSec);
        var db = new SKRect();
        timeFont.MeasureText(durStr, out db, timePaint);
        canvas.DrawText(durStr, right - db.Width, timeY, SKTextAlign.Left, timeFont, timePaint);

        if (Math.Abs(snap.PlaybackRate - 1.0) > 0.001)
        {
            string rate = $"{snap.PlaybackRate:0.0}×";
            canvas.DrawText(rate, left + db.Width + 20f * scale, timeY, SKTextAlign.Left, timeFont, timePaint);
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
        DrawCleanButton(canvas, _shuffleBtn, scale, snap.CanShuffle, snap.Shuffle, accent, text, DrawShuffleIcon);

        // Prev (Clean icon button without glass circle)
        DrawCleanButton(canvas, _prevBtn, scale, snap.CanPrev, false, accent, text, DrawPrevIcon);

        // Play / Pause (Hero Glowing Accent Button)
        bool canPp = snap.IsPlaying ? snap.CanPause : snap.CanPlay;
        DrawHeroPlayButton(canvas, _ppBtn, scale, canPp, snap.IsPlaying, accent, text);

        // Next (Clean icon button without glass circle)
        DrawCleanButton(canvas, _nextBtn, scale, snap.CanNext, false, accent, text, DrawNextIcon);

        // Repeat (Clean icon button without glass circle)
        bool repeatActive = snap.Repeat != MediaPlaybackAutoRepeatMode.None;
        DrawCleanButton(canvas, _repeatBtn, scale, snap.CanRepeat, repeatActive, accent, text,
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

    private static void DrawCleanButton(SKCanvas canvas, SKRect r, float scale, bool enabled, bool active, SKColor accent, SKColor text, Action<SKCanvas, SKRect, SKPaint> drawIcon)
    {
        SKColor iconColor = active ? accent : text.WithAlpha(enabled ? (byte)240 : (byte)70);
        using var iconPaint = new SKPaint { Color = iconColor, IsAntialias = true };
        drawIcon(canvas, r, iconPaint);
    }

    private static void DrawHeroPlayButton(SKCanvas canvas, SKRect r, float scale, bool enabled, bool isPlaying, SKColor accent, SKColor text)
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
            using var numFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), r.Width * 0.24f);
            FontHelper.ConfigureHighQualityFont(numFont);
            using var numPaint = new SKPaint { Color = paint.Color, IsAntialias = true };
            numFont.MeasureText("1", out var nb, numPaint);
            canvas.DrawText("1", cx - nb.Width / 2f, cy + nb.Height / 3f, SKTextAlign.Left, numFont, numPaint);
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
        var snap = _snapshot;
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
            _ = _session?.TryChangeShuffleActiveAsync(!snap.Shuffle);
        }
        else if (_prevBtn.Contains(hitPoint) && snap.CanPrev)
        {
            _ = _session?.TrySkipPreviousAsync();
        }
        else if (_ppBtn.Contains(hitPoint))
        {
            if (snap.IsPlaying && snap.CanPause) _ = _session?.TryPauseAsync();
            else if (!snap.IsPlaying && snap.CanPlay) _ = _session?.TryPlayAsync();
        }
        else if (_nextBtn.Contains(hitPoint) && snap.CanNext)
        {
            _ = _session?.TrySkipNextAsync();
        }
        else if (_repeatBtn.Contains(hitPoint) && snap.CanRepeat)
        {
            var next = snap.Repeat switch
            {
                MediaPlaybackAutoRepeatMode.None => MediaPlaybackAutoRepeatMode.List,
                MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
                _ => MediaPlaybackAutoRepeatMode.None
            };
            _ = _session?.TryChangeAutoRepeatModeAsync(next);
        }
        else if (_badgeBtn.Contains(hitPoint) && _smctManager is not null)
        {
            CycleSession();
        }
        else if (_progressWidth > 0 && snap.Duration.TotalSeconds > 0
                  && Math.Abs(hitPoint.Y - _progressY) <= 24f
                  && hitPoint.X >= _progressLeft
                  && hitPoint.X <= _progressLeft + _progressWidth
                 && snap.CanSeek)
        {
            double ratio = Math.Clamp((hitPoint.X - _progressLeft) / _progressWidth, 0.0, 1.0);
            _ = _session?.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(ratio * snap.Duration.TotalSeconds).Ticks);
        }
    }

    private void CycleSession()
    {
        if (_smctManager is null) return;

        var sessions = _smctManager.GetSessions();
        if (sessions.Count <= 1) return;

        int idx = -1;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (ReferenceEquals(sessions[i], _session))
            {
                idx = i;
                break;
            }
        }

        int nextIdx = (idx + 1) % sessions.Count;
        AttachSession(sessions[nextIdx]);
    }

    // ── Color extraction (from artwork) ───────────────────────────────────

    private void ExtractBackgroundColor()
    {
        if (_albumArt is null)
        {
            _bgColor = new SKColor(18, 18, 24);
            return;
        }

        try
        {
            using var sample = new SKBitmap(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(sample);
            canvas.Clear();
            canvas.DrawBitmap(_albumArt, new SKRect(0, 0, 32, 32), HighQualitySampling);
            canvas.Flush();

            var buckets = new Dictionary<int, (SKColor color, int count, float brightness)>();

            for (int y = 0; y < sample.Height; y++)
            {
                for (int x = 0; x < sample.Width; x++)
                {
                    SKColor px = sample.GetPixel(x, y);
                    float max = Math.Max(Math.Max(px.Red, px.Green), px.Blue);
                    float brightness = max / 255f;

                    if (brightness < 0.10f || brightness > 0.92f) continue;

                    int qR = (px.Red / 16) * 16;
                    int qG = (px.Green / 16) * 16;
                    int qB = (px.Blue / 16) * 16;
                    int key = (qR << 16) | (qG << 8) | qB;

                    if (buckets.TryGetValue(key, out var existing))
                    {
                        if (brightness > existing.brightness)
                            buckets[key] = (px, existing.count + 1, brightness);
                        else
                            buckets[key] = (existing.color, existing.count + 1, existing.brightness);
                    }
                    else
                    {
                        buckets[key] = (px, 1, brightness);
                    }
                }
            }

            if (buckets.Count == 0)
            {
                _bgColor = sample.GetPixel(16, 16);
                return;
            }

            var colorful = buckets.Values
                .Where(b =>
                {
                    float min = Math.Min(Math.Min(b.color.Red, b.color.Green), b.color.Blue);
                    float max = Math.Max(Math.Max(b.color.Red, b.color.Green), b.color.Blue);
                    float sat = max > 0 ? (max - min) / max : 0;
                    float br = max / 255f;
                    return sat >= 0.22f && br >= 0.18f && br <= 0.85f;
                })
                .ToList();

            SKColor selected;
            if (colorful.Count > 0)
            {
                selected = colorful
                    .OrderByDescending(b =>
                    {
                        float min = Math.Min(Math.Min(b.color.Red, b.color.Green), b.color.Blue);
                        float max = Math.Max(Math.Max(b.color.Red, b.color.Green), b.color.Blue);
                        float sat = max > 0 ? (max - min) / max : 0;
                        float br = max / 255f;
                        return br * (0.5f + 0.5f * sat) * (1.0f + Math.Min(0.5f, b.count / 50.0f));
                    })
                    .First()
                    .color;
            }
            else
            {
                selected = buckets.Values
                    .OrderByDescending(b => b.brightness)
                    .ThenByDescending(b => b.count)
                    .First()
                    .color;
            }

            float selBright = Math.Max(Math.Max(selected.Red, selected.Green), selected.Blue) / 255f;
            if (selBright > 0.65f)
            {
                float factor = 0.65f / selBright;
                selected = new SKColor(
                    (byte)Math.Clamp(selected.Red * factor, 0, 255),
                    (byte)Math.Clamp(selected.Green * factor, 0, 255),
                    (byte)Math.Clamp(selected.Blue * factor, 0, 255));
            }

            _bgColor = selected;
        }
        catch
        {
            _bgColor = new SKColor(18, 18, 24);
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

    private static SKColor ParseColor(string hex, SKColor fallback)
        => SKColor.TryParse(hex, out var parsed) ? parsed : fallback;

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

    private static string Sanitize(string? input, string fallback)
    {
        if (string.IsNullOrEmpty(input)) return fallback;
        string clean = new string(input.Where(c => !char.IsControl(c) || c == ' ').Take(256).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
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

        if (_smctManager is not null)
        {
            _smctManager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _smctManager.SessionsChanged -= OnSessionsChanged;
        }
        DetachSessionEvents();
        _smctManager = null;
        _session = null;
        _snapshot = null;
        DisposeArtwork();

        await base.DisposeAsync();
    }
}
