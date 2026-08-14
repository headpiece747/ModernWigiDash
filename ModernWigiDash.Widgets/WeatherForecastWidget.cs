using System.Globalization;
using System.Reflection;
using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("weather_forecast", "Weather Forecast", Category = "Social & Visual")]
public class WeatherForecastWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider, IWidgetLocationSearch, IWidgetEditorProvider
{
    public override SKSize DefaultSize => GridSizePreset.Size5x4.ToSize();

    [WidgetProperty("Location Type", WidgetPropertyType.Choice, "City name, ZIP code, or lat,lon pair", "Fixed Location", "Fixed Location")]
    public string LocationType { get; set; } = "Fixed Location";

    [WidgetProperty("Location", WidgetPropertyType.Text, "City name, ZIP/postal code, or lat,lon (e.g. 40.71,-74.00)", "New York")]
    public string Location { get; set; } = "New York";

    [WidgetProperty("Custom Label", WidgetPropertyType.Text, "Custom title display name override", "")]
    public string CustomLabel { get; set; } = "";

    [WidgetProperty("Unit System", WidgetPropertyType.Choice, "Temperature & speed units", "Fahrenheit (°F, mph)", "Fahrenheit (°F, mph)", "Celsius (°C, km/h)", "Celsius (°C, mph)", "Celsius (°C, m/s)", "Kelvin (K, m/s)")]
    public string UnitSystem { get; set; } = WeatherPresentation.DefaultUnitSystem;

    [WidgetProperty("Layout Mode", WidgetPropertyType.Choice, "Display view style", WeatherLayout.DefaultLayoutMode, "Detailed", "Daily Forecast", "Hourly Forecast", "Current Only", "Compact")]
    public string LayoutMode { get; set; } = WeatherLayout.DefaultLayoutMode;

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary glowing accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Show Humidity", WidgetPropertyType.Boolean, "Display relative humidity metric", true)]
    public bool ShowHumidity { get; set; } = true;

    [WidgetProperty("Show Wind", WidgetPropertyType.Boolean, "Display wind speed & direction", true)]
    public bool ShowWind { get; set; } = true;

    [WidgetProperty("Show Feels Like", WidgetPropertyType.Boolean, "Display apparent temperature", true)]
    public bool ShowFeelsLike { get; set; } = true;

    [WidgetProperty("Show High / Low", WidgetPropertyType.Boolean, "Display today's max and min temp", true)]
    public bool ShowHighLow { get; set; } = true;

    [WidgetProperty("Show Forecast Strip", WidgetPropertyType.Boolean, "Display multi-day forecast strip in Detailed view", true)]
    public bool ShowForecast { get; set; } = true;

    [WidgetProperty("Static Snapshot", WidgetPropertyType.Boolean, "Freeze current weather data as a static snapshot", false)]
    public bool StaticSnapshot { get; set; } = false;

    [WidgetProperty("Latitude", WidgetPropertyType.Text, "Override latitude (e.g. 40.7128). Leave empty to auto-resolve from Location.", "")]
    public string Latitude { get; set; } = "";

    [WidgetProperty("Longitude", WidgetPropertyType.Text, "Override longitude (e.g. -74.0060). Leave empty to auto-resolve from Location.", "")]
    public string Longitude { get; set; } = "";

    [WidgetProperty("Country Code", WidgetPropertyType.Text, "Optional ISO country code (US, DE, CA, JP...) to disambiguate same-named cities worldwide. You can also type \"City, State\" or \"City, Country\" in Location.", "")]
    public string CountryCode { get; set; } = "";

    /// <summary>
    /// Pick from the geocoder's candidates when a city name resolves
    /// ambiguously (e.g. "Victoria" matches Canada, Seychelles, ...). Options
    /// are populated from the last geocode via <see cref="IWidgetPropertyOptionsProvider"/>;
    /// an empty value means "let the automatic ranking decide".
    /// </summary>
    [WidgetProperty("Location Match", WidgetPropertyType.Choice, "Pick the exact place when the city name is ambiguous. Leave empty for automatic.", "")]
    public string LocationMatch { get; set; } = "";

    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (propertyName != nameof(LocationMatch)) return [];

        // Empty candidates: no dropdown yet (the geocode may not have run).
        if (_client.LastCandidates.Count == 0) return [];

        // The empty "Automatic (by ranking)" entry lets a pick be cleared.
        return
        [
            new WidgetPropertyOption("", "Automatic (by ranking)"),
            .. _client.LastCandidates.Select(c => new WidgetPropertyOption(c.Query, c.Label))
        ];
    }

    private readonly WeatherClient _client;

    // ── IWidgetLocationSearch ────────────────────────────────────────────────

    public Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct)
        => _client.SearchCitiesAsync(query, ct);

    public void CommitPick(GeocodeCandidate candidate)
    {
        // Commit all four properties before any fetch can claim the in-flight
        // slot: a single-property fetch mid-sequence would race the pick with
        // mixed state (the new label with the old coordinates — the
        // "exact-coordinates fetch never runs" bug). The one forced fetch
        // below runs with the full pick committed.
        _committingLocationPick = true;
        try
        {
            SetProperty(nameof(Location), candidate.Label);
            SetProperty(nameof(Latitude), candidate.Lat.ToString("F5", CultureInfo.InvariantCulture));
            SetProperty(nameof(Longitude), candidate.Lon.ToString("F5", CultureInfo.InvariantCulture));
            SetProperty(nameof(LocationMatch), "");
        }
        finally
        {
            _committingLocationPick = false;
        }
        RequestRefresh(force: true);
    }

    // ── IWidgetEditorProvider ────────────────────────────────────────────────

    public EditorKind? GetEditorKind(PropertyInfo property)
        => property.Name == nameof(Location) ? EditorKind.LocationSearch : null;

    public WeatherForecastWidget()
    {
        _client = new WeatherClient(CacheDir, $"weather_{InstanceId}.json", logError: (message, exception) => Context?.LogError(message, exception));
    }

    /// <summary>Test seam: injectable clock for fetch throttling and cache timestamps (forwards to the client).</summary>
    internal TimeProvider Clock { get => _client.Clock; set => _client.Clock = value; }

    /// <summary>Test seam: substitute HTTP transport for fetch tests (forwards to the client).</summary>
    internal HttpClient? TestHttpClient { get => _client.TestHttpClient; set => _client.TestHttpClient = value; }

    /// <summary>The last resolved display name (test/UI seam into the client).</summary>
    internal string ResolvedCityName => _client.ResolvedCityName;

    /// <summary>Completed-fetch count (test seam: wait on fetch completion, not call start).</summary>
    internal int FetchCompletedCount => _client.FetchCompletedCount;

    private double _currentTempC = 25.0; // 77°F default
    private double _feelsLikeC = 22.2;  // 72°F default
    private double _humidity = 87.0;
    private double _windSpeedKmH = 16.1; // 10 mph default
    private int _weatherCode = 51;      // Drizzle default
    private double _highTempC = 26.6;   // 80°F default
    private double _lowTempC = 20.5;    // 69°F default

    // The card fill/stroke paints behind every pill, row, and column — one
    // shared pair, colors swapped via Paint.Color mutation (hoisted out of the
    // per-card loops).
    private readonly SKPaint _cardFillPaint = new() { IsAntialias = true };
    private readonly SKPaint _cardStrokePaint = new() { Style = SKPaintStyle.Stroke, IsAntialias = true };
    private readonly SKPaint _metricPaint = new() { IsAntialias = true };
    private readonly SKPaint _dayPaint = new() { IsAntialias = true };
    private readonly SKPaint _iconPaint = new() { IsAntialias = true };
    private readonly SKPaint _descPaint = new() { IsAntialias = true };
    private readonly SKPaint _tempPaint = new() { IsAntialias = true };
    private readonly SKPaint _timePaint = new() { IsAntialias = true };
    private readonly SKPaint _rangePaint = new() { IsAntialias = true };
    private readonly SKPaint _dayIconPaint = new() { IsAntialias = true };

    internal readonly List<DailyForecastItem> _dailyForecasts = [];
    internal readonly List<HourlyForecastItem> _hourlyForecasts = [];
    private readonly Lock _forecastGate = new();
    private IReadOnlyList<DailyForecastItem> _dailyForecastSnapshot = [];
    private IReadOnlyList<HourlyForecastItem> _hourlyForecastSnapshot = [];
    private int _forecastVersion;
    private int _renderedForecastVersion = -1;
    private SKRect _lastBounds;

    /// <summary>The "select which one" gate state (test seam): true while the
    /// last resolution was an ambiguous tie the user must pick from — the
    /// render draws the prompt instead of any data. Cleared by a successful
    /// fetch and by any location property change before the forced re-fetch.</summary>
    internal bool _needsLocationSelection;

    // The render-model cache: every formatted string the draw paths need is
    // rebuilt only when (data version, bounds, property snapshot) changes —
    // weather data moves at most every 15 minutes, so the static scene
    // allocates nothing on the 30 FPS render path.
    private int _dataVersion;
    private WeatherRenderModel? _renderModel;

    private static readonly string CacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "weather_cache");
    private PollLoop? _refreshPoll;
    private CancellationTokenSource? _pollCts;

    public override ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(context, cancellationToken);
        _ = LoadCachedWeatherAsync();
        _pollCts = new CancellationTokenSource();
        // The 15-min refresh rides the repo's one loop shape (the old code
        // used the last raw System.Threading.Timer: fire-and-forget async
        // callback, no readiness guard, no failure logging).
        _refreshPoll = new PollLoop(
            "WEATHER", TimeSpan.FromMinutes(15), () => true,
            WeatherRefreshTick, () => { }, msg => Context?.LogInfo(msg));
        _refreshPoll.Start();
        _ = FetchLiveWeatherAsync();
        return ValueTask.CompletedTask;
    }

    private void WeatherRefreshTick() => RequestRefresh();

    /// <summary>
    /// The single "fetch if due" gate for every cadence source: the 15-min
    /// refresh PollLoop, the per-frame render kick, and the property-change
    /// force paths all call this. The static-snapshot rule and the client's
    /// throttle-window pre-check are applied here, once, instead of being
    /// re-derived at each call site; the client's atomic in-flight claim
    /// remains the authority (see <see cref="WeatherClient.TryBeginFetch"/>).
    /// </summary>
    private void RequestRefresh(bool force = false)
    {
        if (!force && (IsStaticSnapshotBlocking || !_client.IsFetchWindowElapsed())) return;
        _ = FetchLiveWeatherAsync(force);
    }

    public override async ValueTask DisposeAsync()
    {
        _refreshPoll?.Dispose();
        if (_pollCts != null)
        {
            await _pollCts.CancelAsync();
            _pollCts.Dispose();
        }
        await base.DisposeAsync();
    }

    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        // A Location Match pick resolves against the candidates it was offered
        // from, so it keeps them (InvalidateCoordinates); every other location
        // change clears the candidates so a stale pick can never win. The
        // property fetches are suppressed while CommitPick writes the whole
        // pick — it fires the one exact-coordinates fetch itself.
        if (propertyName == nameof(LocationMatch))
        {
            _client.InvalidateCoordinates();
            if (!_committingLocationPick) RequestRefresh(force: true);
        }
        else if (propertyName is nameof(Location) or nameof(Latitude) or nameof(Longitude) or nameof(CountryCode))
        {
            // A location change is the user answering the "select which one"
            // prompt — drop the gate state before the forced re-fetch so the
            // widget renders live data again (or re-enters the prompt if the
            // new location is ambiguous too).
            _needsLocationSelection = false;
            _client.InvalidateLocation();
            if (!_committingLocationPick) RequestRefresh(force: true);
        }
        base.OnPropertyChanged(propertyName, newValue);
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor accentColor = ColorOf(AccentColorHex, new SKColor(255, 205, 133));
        SKColor textPrimary = SKColors.White;
        SKColor textSecondary = SKColors.White;

        // The select-which-one prompt replaces the whole data render — and it
        // skips the fetch kick too (the ambiguous resolution already ran; a
        // pick or a location change clears the flag before forcing a re-fetch).
        if (_needsLocationSelection)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, $"{Location} — select which one",
                "Open the inspector and pick the exact place", textPrimary);
            return;
        }

        // Kick the fetch through the one cadence gate: the static-snapshot
        // rule and the client's throttle window are applied in RequestRefresh,
        // so the per-frame async allocation is skipped while the window is
        // open, and the render tick never re-derives the fetch policy.
        RequestRefresh();

        _lastBounds = bounds;

        // Snapshot the forecast lists so the fetch thread's swaps never mutate
        // a list mid-render — but only when the source actually changed (the
        // snapshot copies are skipped on the frames in between).
        lock (_forecastGate)
        {
            if (_renderedForecastVersion != _forecastVersion)
            {
                _renderedForecastVersion = _forecastVersion;
                _dailyForecastSnapshot = _dailyForecasts.ToArray();
                _hourlyForecastSnapshot = _hourlyForecasts.ToArray();
            }
        }

        var (sx, sy, s) = WeatherLayout.Scale(bounds);
        var header = WeatherLayout.ComputeHeader(bounds, s, sy);
        var (tempUnit, speedUnit) = WeatherPresentation.ParseUnitSystem(UnitSystem);

        // The render model owns every formatted string; the draw paths only
        // measure and paint (the model rebuilds when its key components change).
        var model = EnsureRenderModel(bounds, tempUnit, speedUnit);

        // Prominent Location Name Header
        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, header.TitleFontSize);
        using var titlePaint = new SKPaint { Color = textPrimary, IsAntialias = true };
        canvas.DrawTextWithFallback(model.TruncatedHeader, bounds.Left + header.Pad, header.HeaderTextY, titleFont, titlePaint);

        // Styled Unit Toggle Badge [°F] / [°C] (No background card)
        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(17f * s, 10f, 30f));
        using var unitPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float uW = FontHelper.MeasureTextWithFallback(tempUnit, unitFont);
        canvas.DrawTextWithFallback(tempUnit, header.BadgeRect.MidX - uW / 2f, header.BadgeRect.MidY + 4.5f * s, unitFont, unitPaint);

        // Content Area Bounds
        SKRect contentBounds = new(bounds.Left + header.Pad, bounds.Top + header.HeaderHeight + 6f * sy, bounds.Right - header.Pad, bounds.Bottom - header.Pad);

        switch (WeatherLayout.ParseMode(LayoutMode))
        {
            case WeatherLayoutMode.DailyForecast:
                RenderDailyForecast(canvas, contentBounds, accentColor, textPrimary, textSecondary, sx, sy, model);
                break;
            case WeatherLayoutMode.HourlyForecast:
                RenderHourlyForecast(canvas, contentBounds, accentColor, textSecondary, sx, sy, model);
                break;
            case WeatherLayoutMode.CurrentOnly:
                RenderCurrentOnly(canvas, contentBounds, accentColor, textPrimary, sx, sy, model);
                break;
            case WeatherLayoutMode.Compact:
                RenderCompact(canvas, contentBounds, textPrimary, sx, sy, model);
                break;
            default:
                RenderDetailed(canvas, contentBounds, accentColor, textPrimary, textSecondary, sx, sy, model);
                break;
        }
    }

    private void RenderDetailed(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, float sx, float sy, WeatherRenderModel model)
    {
        var (icon, desc) = WeatherPresentation.MapWmoCode(_weatherCode);
        float s = Math.Min(sx, sy);
        float w = bounds.Width;
        float h = bounds.Height;

        // Show forecast strip only if container height is at least 150px physical units
        bool hasForecast = ShowForecast && _dailyForecastSnapshot.Count > 0 && h >= 150f;
        float forecastH = hasForecast ? Math.Clamp(80f * sy, 45f, 160f) : 0f;

        // Show metrics pill strip only if container height is at least 150px physical units
        bool hasMetrics = model.Metrics.Count > 0 && h >= 150f;
        float metricsH = hasMetrics ? Math.Clamp(28f * sy, 16f, 50f) : 0f;

        float heroTop = bounds.Top + 4f * sy;
        float heroBottom = bounds.Bottom - forecastH - (hasMetrics ? metricsH + 12f * sy : 0f) - 4f * sy;
        float heroHeight = Math.Max(heroBottom - heroTop, 35f);
        float heroMidY = heroTop + heroHeight / 2f;

        // Sizing hero elements proportionally to fit strictly inside heroHeight without overlapping pills below
        float iconSize = Math.Clamp(heroHeight * 0.75f, 20f, 220f);
        float tempSize = Math.Clamp(heroHeight * 0.45f, 14f, 140f);
        float descSize = Math.Clamp(heroHeight * 0.18f, 9f, 45f);

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, iconSize);
        using var iconPaint = new SKPaint { IsAntialias = true };
        float iconW = iconFont.MeasureText(icon);

        string mainTempStr = model.MainTemp;
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, tempSize);
        using var tempPaint = new SKPaint { Color = textPrimary, IsAntialias = true };

        var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, descSize);
        using var descPaint = new SKPaint { Color = accentColor, IsAntialias = true };

        // Ensure vertical text stack (Temp + Condition) strictly fits inside heroHeight
        tempFont.GetFontMetrics(out var tempMetrics);
        descFont.GetFontMetrics(out var descMetrics);
        float tempH = tempMetrics.Descent - tempMetrics.Ascent;
        float descH = descMetrics.Descent - descMetrics.Ascent;
        float textStackSpacing = 2f * sy;
        float textStackTotalH = tempH + textStackSpacing + descH;

        float fitScale = WeatherLayout.HeroTextStackShrinkScale(textStackTotalH, heroHeight);
        if (fitScale < 1f)
        {
            tempSize *= fitScale;
            descSize *= fitScale;
            tempFont.Size = tempSize;
            descFont.Size = descSize;

            tempFont.GetFontMetrics(out tempMetrics);
            descFont.GetFontMetrics(out descMetrics);
            tempH = tempMetrics.Descent - tempMetrics.Ascent;
            descH = descMetrics.Descent - descMetrics.Ascent;
            textStackTotalH = tempH + textStackSpacing + descH;
        }

        float tempW = tempFont.MeasureText(mainTempStr);
        float descW = descFont.MeasureText(desc);

        float rightBlockW = Math.Max(tempW, descW);
        float gap = Math.Clamp(20f * s, 8f, 50f);
        float totalBlockW = iconW + gap + rightBlockW;

        // Auto-scale hero block down if container is narrow
        if (totalBlockW > w)
        {
            float scaleFactor = Math.Max(0.5f, w / totalBlockW);
            iconSize *= scaleFactor;
            tempSize *= scaleFactor;
            descSize *= scaleFactor;
            gap *= scaleFactor;

            iconFont.Size = iconSize;
            tempFont.Size = tempSize;
            descFont.Size = descSize;

            iconW = iconFont.MeasureText(icon);
            tempW = tempFont.MeasureText(mainTempStr);
            descW = descFont.MeasureText(desc);
            rightBlockW = Math.Max(tempW, descW);
            totalBlockW = iconW + gap + rightBlockW;
        }

        float blockLeft = bounds.MidX - totalBlockW / 2f;
        float rightX = blockLeft + iconW + gap;

        // Draw Icon perfectly centered vertically beside Temp + Condition
        iconFont.GetFontMetrics(out var iconMetrics);
        float iconBaseline = heroMidY - (iconMetrics.Ascent + iconMetrics.Descent) / 2f;
        canvas.DrawTextWithFallback(icon, blockLeft, iconBaseline, iconFont, iconPaint);

        // Stack Temperature & Condition on right of icon with centered vertical alignment
        float textStackTop = heroMidY - textStackTotalH / 2f;
        float tempBaseline = textStackTop - tempMetrics.Ascent;
        float descBaseline = tempBaseline + tempMetrics.Descent + textStackSpacing - descMetrics.Ascent;

        canvas.DrawTextWithFallback(mainTempStr, rightX, tempBaseline, tempFont, tempPaint);
        canvas.DrawTextWithFallback(desc, rightX, descBaseline, descFont, descPaint);

        RenderMetricPills(canvas, bounds, hasMetrics, metricsH, heroBottom, textSecondary, sx, sy, model);
        RenderForecastStrip(canvas, bounds, hasForecast, forecastH, accentColor, textPrimary, textSecondary, sx, sy, model);
    }

    private void RenderMetricPills(SKCanvas canvas, SKRect bounds, bool hasMetrics, float metricsH, float heroBottom, SKColor textSecondary, float sx, float sy, WeatherRenderModel model)
    {
        if (!hasMetrics) return;

        float s = Math.Min(sx, sy);
        float w = bounds.Width;
        float pillY = heroBottom + 4f * sy;
        float pillHeight = metricsH;
        float metricFontSize = Math.Clamp(13f * s, 8f, 24f);
        var metricFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, metricFontSize);

        float pillPadX = Math.Clamp(10f * s, 4f, 20f);
        float pillGap = Math.Clamp(8f * s, 3f, 16f);
        float totalPillsW = 0f;
        float[] metricWidths = model.MetricWidths;
        for (int i = 0; i < metricWidths.Length; i++)
        {
            totalPillsW += metricWidths[i];
        }
        totalPillsW += (model.Metrics.Count - 1) * pillGap;

        // If pills exceed bounds width, scale down metric font size to fit inside card
        float metricScale = WeatherLayout.MetricPillShrinkScale(totalPillsW, w);
        if (metricScale < 1f)
        {
            metricFontSize = Math.Max(7f, metricFontSize * metricScale);
            metricFont.Size = metricFontSize;
            pillPadX *= metricScale;
            pillGap *= metricScale;

            totalPillsW = 0f;
            for (int i = 0; i < model.Metrics.Count; i++)
            {
                metricWidths[i] = metricFont.MeasureText(model.Metrics[i]) + pillPadX * 2;
                totalPillsW += metricWidths[i];
            }
            totalPillsW += (model.Metrics.Count - 1) * pillGap;
        }

        _cardStrokePaint.Color = new SKColor(255, 255, 255, 22);
        _cardStrokePaint.StrokeWidth = Math.Max(1f * s, 1f);
        _metricPaint.Color = textSecondary;

        metricFont.GetFontMetrics(out var mMetrics);
        float mBaseline = pillY + pillHeight / 2f - (mMetrics.Ascent + mMetrics.Descent) / 2f;

        float pillStartX = bounds.MidX - totalPillsW / 2f;
        for (int i = 0; i < model.Metrics.Count; i++)
        {
            SKRect pillRect = new(pillStartX, pillY, pillStartX + metricWidths[i], pillY + pillHeight);
            canvas.DrawRoundRect(pillRect, 8f * s, 8f * s, _cardStrokePaint);
            canvas.DrawTextWithFallback(model.Metrics[i], pillRect.MidX, mBaseline, metricFont, _metricPaint, SKTextAlign.Center);
            pillStartX += metricWidths[i] + pillGap;
        }
    }

    private void RenderForecastStrip(SKCanvas canvas, SKRect bounds, bool hasForecast, float forecastH, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, float sx, float sy, WeatherRenderModel model)
    {
        if (!hasForecast) return;

        float s = Math.Min(sx, sy);
        float w = bounds.Width;
        int count = Math.Min(_dailyForecastSnapshot.Count, 5);
        float stripY = bounds.Bottom - forecastH;
        SKRect stripBounds = new(bounds.Left, stripY, bounds.Right, bounds.Bottom);

        _cardStrokePaint.Color = new SKColor(255, 255, 255, 18);
        _cardStrokePaint.StrokeWidth = Math.Max(1f * s, 1f);
        canvas.DrawRoundRect(stripBounds, 12f * s, 12f * s, _cardStrokePaint);

        float colWidth = w / count;
        float dayFontSize = Math.Clamp(14f * s, 8f, 24f);
        float dayIconFontSize = Math.Clamp(22f * s, 10f, 48f);
        float rangeFontSize = Math.Clamp(12f * s, 7f, 22f);

        var dayFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, dayFontSize);
        var rangeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, rangeFontSize);
        var dayIconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Normal, dayIconFontSize);

        _rangePaint.Color = textSecondary;
        _dayIconPaint.Color = SKColors.Black;

        for (int i = 0; i < count; i++)
        {
            var day = _dailyForecastSnapshot[i];
            var (dayIcon, _) = WeatherPresentation.MapWmoCode(day.WeatherCode);
            float colCx = bounds.Left + (i + 0.5f) * colWidth;

            _dayPaint.Color = i == 0 ? accentColor : textPrimary;
            float dayY = stripY + Math.Clamp(18f * s, 10f, 36f);

            dayFont.MeasureText(day.DayName, out var dayBounds);
            float dayX = colCx - (dayBounds.Left + dayBounds.Width / 2f);
            canvas.DrawTextWithFallback(day.DayName, dayX, dayY, dayFont, _dayPaint);

            string rangeStr = model.ForecastRanges[i];
            float rangeY = stripBounds.Bottom - Math.Clamp(10f * s, 5f, 20f);

            rangeFont.MeasureText(rangeStr, out var rangeBounds);
            float rangeX = colCx - (rangeBounds.Left + rangeBounds.Width / 2f);
            canvas.DrawTextWithFallback(rangeStr, rangeX, rangeY, rangeFont, _rangePaint);

            // Calculate exact vertical center between Day Name and Temp Range baselines
            dayFont.GetFontMetrics(out var dayMetrics);
            rangeFont.GetFontMetrics(out var rangeMetrics);
            dayIconFont.GetFontMetrics(out var dayIconMetrics);

            float dayBottomY = dayY + dayMetrics.Descent;
            float rangeTopY = rangeY + rangeMetrics.Ascent;
            float midGapY = (dayBottomY + rangeTopY) / 2f;
            float dayIconBaseline = midGapY - (dayIconMetrics.Ascent + dayIconMetrics.Descent) / 2f;

            // Exact visual bounding box horizontal centering for emoji icon
            dayIconFont.MeasureText(dayIcon, out var iconRect);
            float iconVisualCenterX = iconRect.Left + (iconRect.Width / 2f);
            float iconX = colCx - iconVisualCenterX;

            canvas.DrawTextWithFallback(dayIcon, iconX, dayIconBaseline, dayIconFont, _dayIconPaint);
        }
    }

    private void RenderDailyForecast(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, float sx, float sy, WeatherRenderModel model)
    {
        int count = Math.Min(_dailyForecastSnapshot.Count, 5);
        if (count == 0) return;

        float rowHeight = bounds.Height / count;
        float s = Math.Min(sx, sy);

        _cardFillPaint.Color = new SKColor(22, 26, 40, 180);
        _cardStrokePaint.Color = new SKColor(255, 255, 255, 15);
        _cardStrokePaint.StrokeWidth = 1f;
        _descPaint.Color = textSecondary;
        _tempPaint.Color = accentColor;
        _iconPaint.Color = SKColors.Black;

        var dayFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(13f * s, 9f, 18f));
        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Normal, Math.Clamp(16f * s, 10f, 22f));
        var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Clamp(11f * s, 8f, 15f));
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(12f * s, 8f, 16f));

        for (int i = 0; i < count; i++)
        {
            var day = _dailyForecastSnapshot[i];
            float y = bounds.Top + (i * rowHeight);
            SKRect rowRect = new(bounds.Left, y + 2, bounds.Right, y + rowHeight - 2);

            canvas.DrawRoundRect(rowRect, 8f * s, 8f * s, _cardFillPaint);
            canvas.DrawRoundRect(rowRect, 8f * s, 8f * s, _cardStrokePaint);

            var (icon, desc) = WeatherPresentation.MapWmoCode(day.WeatherCode);

            _dayPaint.Color = i == 0 ? accentColor : textPrimary;
            canvas.DrawTextWithFallback(day.DayName, rowRect.Left + 12f * sx, rowRect.MidY + 5f * sy, dayFont, _dayPaint);

            canvas.DrawTextWithFallback(icon, rowRect.Left + 80f * sx, rowRect.MidY + 6f * sy, iconFont, _iconPaint);

            canvas.DrawTextWithFallback(desc, rowRect.Left + 110f * sx, rowRect.MidY + 4f * sy, descFont, _descPaint);

            string highLowStr = model.DailyHighLows[i];
            canvas.DrawTextWithFallback(highLowStr, rowRect.Right - FontHelper.MeasureTextWithFallback(highLowStr, tempFont) - 12f * sx, rowRect.MidY + 4f * sy, tempFont, _tempPaint);
        }
    }

    private void RenderHourlyForecast(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textSecondary, float sx, float sy, WeatherRenderModel model)
    {
        int count = Math.Min(_hourlyForecastSnapshot.Count, 6);
        if (count == 0) return;

        float itemWidth = bounds.Width / count;
        float s = Math.Min(sx, sy);

        _cardFillPaint.Color = new SKColor(22, 26, 40, 180);
        _cardStrokePaint.Color = new SKColor(255, 255, 255, 15);
        _cardStrokePaint.StrokeWidth = 1f;
        _timePaint.Color = textSecondary;
        _tempPaint.Color = accentColor;
        _iconPaint.Color = SKColors.Black;

        var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(11f * s, 8f, 15f));
        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Normal, Math.Clamp(20f * s, 12f, 28f));
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(12f * s, 8f, 16f));

        for (int i = 0; i < count; i++)
        {
            var item = _hourlyForecastSnapshot[i];
            float x = bounds.Left + (i * itemWidth);
            SKRect colRect = new(x + 2, bounds.Top + 4, x + itemWidth - 2, bounds.Bottom - 4);

            canvas.DrawRoundRect(colRect, 8f * s, 8f * s, _cardFillPaint);
            canvas.DrawRoundRect(colRect, 8f * s, 8f * s, _cardStrokePaint);

            var (icon, _) = WeatherPresentation.MapWmoCode(item.WeatherCode);

            canvas.DrawTextWithFallback(item.TimeLabel, colRect.MidX - (FontHelper.MeasureTextWithFallback(item.TimeLabel, timeFont) / 2f), colRect.Top + 22f * sy, timeFont, _timePaint);

            canvas.DrawTextWithFallback(icon, colRect.MidX - 12f * sx, colRect.MidY + 6f * sy, iconFont, _iconPaint);

            string tempStr = model.HourlyTemps[i];
            canvas.DrawTextWithFallback(tempStr, colRect.MidX - (FontHelper.MeasureTextWithFallback(tempStr, tempFont) / 2f), colRect.Bottom - 14f * sy, tempFont, _tempPaint);
        }
    }

    private void RenderCurrentOnly(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, float sx, float sy, WeatherRenderModel model)
    {
        var (icon, desc) = WeatherPresentation.MapWmoCode(_weatherCode);
        float s = Math.Min(sx, sy);
        float midY = bounds.MidY;
        float midX = bounds.MidX;

        float iconSize = Math.Clamp(88f * s, 40f, 120f);
        float tempSize = Math.Clamp(64f * s, 28f, 84f);
        float descSize = Math.Clamp(24f * s, 12f, 32f);

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, iconSize);
        using var iconPaint = new SKPaint { IsAntialias = true };
        float iconW = iconFont.MeasureText(icon);

        string mainTempStr = model.MainTemp;
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, tempSize);
        using var tempPaint = new SKPaint { Color = textPrimary, IsAntialias = true };
        float tempW = tempFont.MeasureText(mainTempStr);

        var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, descSize);
        using var descPaint = new SKPaint { Color = accentColor, IsAntialias = true };
        float descW = descFont.MeasureText(desc);

        float rightBlockW = Math.Max(tempW, descW);
        float gap = 24f * sx;
        float totalBlockW = iconW + gap + rightBlockW;
        float blockLeft = midX - totalBlockW / 2f;
        float rightX = blockLeft + iconW + gap;

        iconFont.GetFontMetrics(out var iconMetrics);
        float iconBaseline = midY - (iconMetrics.Ascent + iconMetrics.Descent) / 2f;
        canvas.DrawTextWithFallback(icon, blockLeft, iconBaseline, iconFont, iconPaint);

        tempFont.GetFontMetrics(out var tempMetrics);
        descFont.GetFontMetrics(out var descMetrics);
        float tempH = tempMetrics.Descent - tempMetrics.Ascent;
        float descH = descMetrics.Descent - descMetrics.Ascent;
        float textStackTotalH = tempH + 6f * sy + descH;
        float textStackTop = midY - textStackTotalH / 2f;

        float tempBaseline = textStackTop - tempMetrics.Ascent;
        float descBaseline = tempBaseline + tempMetrics.Descent + 6f * sy - descMetrics.Ascent;

        canvas.DrawTextWithFallback(mainTempStr, rightX, tempBaseline, tempFont, tempPaint);
        canvas.DrawTextWithFallback(desc, rightX, descBaseline, descFont, descPaint);
    }

    private void RenderCompact(SKCanvas canvas, SKRect bounds, SKColor textPrimary, float sx, float sy, WeatherRenderModel model)
    {
        var (icon, _) = WeatherPresentation.MapWmoCode(_weatherCode);
        float s = Math.Min(sx, sy);

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, Math.Clamp(26f * s, 14f, 32f));
        using var iconPaint = new SKPaint { IsAntialias = true };
        canvas.DrawTextWithFallback(icon, bounds.Left, bounds.MidY + 10f * sy, iconFont, iconPaint);

        string mainTempStr = model.MainTemp;
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(20f * s, 12f, 26f));
        using var tempPaint = new SKPaint { Color = textPrimary, IsAntialias = true };
        canvas.DrawTextWithFallback(mainTempStr, bounds.Left + 36f * sx, bounds.MidY + 8f * sy, tempFont, tempPaint);
    }

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType != TouchEventType.TouchUp) return;

        // Hit-test against the last rendered bounds so touches line up with the
        // drawn controls at any widget size, not just the design size. The zones
        // come from WeatherLayout — the same geometry the render path draws.
        var b = _lastBounds.Width > 0 ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
        var scale = WeatherLayout.Scale(b);

        switch (WeatherLayout.GetHeaderAction(b, localPoint, scale.S, scale.Sy))
        {
            case WeatherHeaderAction.ToggleUnit:
                SetProperty(nameof(UnitSystem), WeatherPresentation.ToggleUnitSystem(UnitSystem));
                return;
            case WeatherHeaderAction.CycleLayout:
                SetProperty(nameof(LayoutMode), WeatherLayout.DisplayName(
                    WeatherLayout.NextMode(LayoutMode)));
                return;
            default:
                _ = FetchLiveWeatherAsync(force: true);
                break;
        }
    }

    /// <summary>
    /// Fetches live weather through the client's atomic fetch claim — the
    /// in-flight/throttle decision is the client's, single-sourced. While a
    /// static snapshot is showing, non-forced fetches are blocked.
    /// </summary>
    private bool IsStaticSnapshotBlocking => StaticSnapshot && _client.LastFetchTimeUtc != DateTime.MinValue;

    /// <summary>
    /// The candidate labels last pushed to the inspector. Refreshing the
    /// inspector rebuilds the panel, which steals focus from the field the
    /// user is typing in — so a refresh fires only when the pickable options
    /// actually changed (e.g. the first geocode after a Location edit), never
    /// on every fetch.
    /// </summary>
    private string _lastInspectorCandidatesStamp = "";
    private bool _committingLocationPick = false;

    internal async Task FetchLiveWeatherAsync(bool force = false)
    {
        if (IsStaticSnapshotBlocking && !force) return;

        var snapshot = await _client.FetchCurrentAsync(BuildLocation(), force, _pollCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        if (snapshot is null)
        {
            // The ambiguity gate: an untrusted location tie never shows weather.
            if (_client.LastResolutionAmbiguous)
            {
                _needsLocationSelection = true;
                Context?.RequestRender();
            }
            return;
        }
        _needsLocationSelection = false;

        ApplySnapshot(snapshot);

        // The geocode may have produced new Location Match candidates: refresh
        // the inspector so an already-open panel shows the dropdown (the Twitch
        // pattern — the renderer only builds a ComboBox when options exist).
        // Only when the option set changed — see _lastInspectorCandidatesStamp.
        string stamp = string.Join('\n', _client.LastCandidates.Select(c => c.Query));
        if (stamp != _lastInspectorCandidatesStamp)
        {
            _lastInspectorCandidatesStamp = stamp;
            Context?.RequestInspectorRefresh();
        }

        Context?.RequestRender();
    }

    private WeatherLocation BuildLocation()
        => new(LocationType, Location, Latitude, Longitude, CustomLabel, string.IsNullOrWhiteSpace(CountryCode) ? null : CountryCode.Trim())
        {
            LocationMatch = string.IsNullOrWhiteSpace(LocationMatch) ? null : LocationMatch.Trim()
        };

    /// <summary>
    /// Applies a fetched/cached snapshot to the render fields, keeping the
    /// "response omitted this section → keep the previous value" semantics.
    /// </summary>
    private void ApplySnapshot(WeatherSnapshot snapshot)
    {
        lock (_forecastGate)
        {
            _dataVersion++;
            if (snapshot.CurrentTempC is not null) _currentTempC = snapshot.CurrentTempC.Value;
            if (snapshot.FeelsLikeC is not null) _feelsLikeC = snapshot.FeelsLikeC.Value;
            if (snapshot.Humidity is not null) _humidity = snapshot.Humidity.Value;
            if (snapshot.WindSpeedKmH is not null) _windSpeedKmH = snapshot.WindSpeedKmH.Value;
            if (snapshot.WeatherCode is not null) _weatherCode = snapshot.WeatherCode.Value;
            if (snapshot.HighTempC is not null) _highTempC = snapshot.HighTempC.Value;
            if (snapshot.LowTempC is not null) _lowTempC = snapshot.LowTempC.Value;
            if (snapshot.DailyForecasts is not null)
            {
                _dailyForecasts.Clear();
                _dailyForecasts.AddRange(snapshot.DailyForecasts);
                _forecastVersion++;
            }
            if (snapshot.HourlyForecasts is not null)
            {
                _hourlyForecasts.Clear();
                _hourlyForecasts.AddRange(snapshot.HourlyForecasts);
                _forecastVersion++;
            }
        }
    }

    private async Task LoadCachedWeatherAsync()
    {
        var cached = await _client.LoadCacheAsync().ConfigureAwait(false);
        if (cached is not null) ApplySnapshot(cached);
    }

    /// <summary>
    /// Returns the cached render model for the current frame, rebuilding it
    /// when (data version, bounds, property snapshot) no longer matches. The
    /// pill widths are measured here with the same pill font size the draw
    /// path derives from the bounds, so the per-frame path allocates no
    /// strings and no arrays for the static scene.
    /// </summary>
    private WeatherRenderModel EnsureRenderModel(SKRect bounds, string tempUnit, string speedUnit)
    {
        // One snapshot under the gate: the version and the seven scalars are
        // written by the fetch thread; the model must build from one
        // consistent view (a torn version read would freeze the cache key).
        double currentTempC, feelsLikeC, humidity, windSpeedKmH, highTempC, lowTempC;
        int dataVersion;
        lock (_forecastGate)
        {
            dataVersion = _dataVersion;
            currentTempC = _currentTempC;
            feelsLikeC = _feelsLikeC;
            humidity = _humidity;
            windSpeedKmH = _windSpeedKmH;
            highTempC = _highTempC;
            lowTempC = _lowTempC;
        }

        if (_renderModel is { } cached && IsCacheValid(cached, dataVersion, bounds))
        {
            return cached;
        }

        var (sx, sy, s) = WeatherLayout.Scale(bounds);
        var header = WeatherLayout.ComputeHeader(bounds, s, sy);

        var model = new WeatherRenderModel
        {
            DataVersion = dataVersion,
            Bounds = bounds,
            LayoutMode = LayoutMode,
            UnitSystem = UnitSystem,
            CustomLabel = CustomLabel,
            ResolvedCity = _client.ResolvedCityName,
            ShowFeelsLike = ShowFeelsLike,
            ShowHumidity = ShowHumidity,
            ShowWind = ShowWind,
            ShowHighLow = ShowHighLow,
            ShowForecast = ShowForecast,
            MainTemp = WeatherPresentation.FormatTemp(currentTempC, tempUnit),
            Metrics = WeatherPresentation.MetricPills(new WeatherMetricsInput(
                ShowFeelsLike, feelsLikeC,
                ShowHumidity, humidity,
                ShowWind, windSpeedKmH,
                ShowHighLow, highTempC, lowTempC,
                tempUnit, speedUnit))
        };

        // Auto-truncated header: the city name uppercased once per model, then
        // truncated to the same max width the draw path uses.
        string cityRaw = string.IsNullOrWhiteSpace(CustomLabel) ? _client.ResolvedCityName : CustomLabel;
        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, header.TitleFontSize);
        float maxTitleW = Math.Max(30f, bounds.Width - header.Pad * 2f - header.BadgeRect.Width);
        model.TruncatedHeader = TextRenderHelper.TruncateText(cityRaw.ToUpperInvariant(), titleFont, maxTitleW);

        // Pill widths: measured with the pill font the draw path derives from
        // the same bounds (the pill shrink re-measures when it triggers).
        model.MetricWidths = MeasurePillWidths(model.Metrics, Math.Min(sx, sy));

        var (ranges, highLows) = BuildForecastStrings(_dailyForecastSnapshot, Math.Min(_dailyForecastSnapshot.Count, 5), tempUnit);
        model.ForecastRanges = ranges;
        model.DailyHighLows = highLows;

        model.HourlyTemps = BuildHourlyStrings(_hourlyForecastSnapshot, tempUnit);

        _renderModel = model;
        return model;
    }

    /// <summary>The render-model cache key: the data version, the bounds
    /// (layout-derived font sizes), and the property snapshot that changes any
    /// formatted string.</summary>
    private bool IsCacheValid(WeatherRenderModel cached, int dataVersion, SKRect bounds)
        => cached.DataVersion == dataVersion
            && cached.Bounds == bounds
            && cached.LayoutMode == LayoutMode
            && cached.UnitSystem == UnitSystem
            && cached.CustomLabel == CustomLabel
            && cached.ResolvedCity == _client.ResolvedCityName
            && cached.ShowFeelsLike == ShowFeelsLike
            && cached.ShowHumidity == ShowHumidity
            && cached.ShowWind == ShowWind
            && cached.ShowHighLow == ShowHighLow
            && cached.ShowForecast == ShowForecast;

    /// <summary>Measured pill widths (text + padding) in the pill font the draw
    /// path derives from the same scale.</summary>
    private static float[] MeasurePillWidths(IReadOnlyList<string> metrics, float scale)
    {
        var metricFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Clamp(13f * scale, 8f, 24f));
        float pillPadX = Math.Clamp(10f * scale, 4f, 20f);
        var widths = new float[metrics.Count];
        for (int i = 0; i < widths.Length; i++)
        {
            widths[i] = metricFont.MeasureText(metrics[i]) + pillPadX * 2;
        }
        return widths;
    }

    private static (string[] Ranges, string[] HighLows) BuildForecastStrings(IReadOnlyList<DailyForecastItem> daily, int count, string tempUnit)
    {
        var ranges = new string[count];
        var highLows = new string[count];
        for (int i = 0; i < count; i++)
        {
            var day = daily[i];
            ranges[i] = WeatherPresentation.ForecastRangeText(day.MaxTempC, day.MinTempC, tempUnit);
            highLows[i] = WeatherPresentation.DailyHighLowText(day.MaxTempC, day.MinTempC, tempUnit);
        }
        return (ranges, highLows);
    }

    private static string[] BuildHourlyStrings(IReadOnlyList<HourlyForecastItem> hourly, string tempUnit)
    {
        int count = Math.Min(hourly.Count, 6);
        var temps = new string[count];
        for (int i = 0; i < count; i++)
        {
            temps[i] = WeatherPresentation.FormatTemp(hourly[i].TempC, tempUnit);
        }
        return temps;
    }

    /// <summary>
    /// The cached render model: every formatted string the five layout modes
    /// draw, recomputed only when its key components change. The key covers
    /// everything that can change the strings — the data version, the bounds
    /// (layout-derived font sizes), and the property snapshot (mode, unit
    /// system, custom label, visibility toggles).
    /// </summary>
    private sealed class WeatherRenderModel
    {
        public int DataVersion = int.MinValue;
        public SKRect Bounds;
        public string LayoutMode = "";
        public string UnitSystem = "";
        public string CustomLabel = "";
        public string ResolvedCity = "";
        public bool ShowFeelsLike;
        public bool ShowHumidity;
        public bool ShowWind;
        public bool ShowHighLow;
        public bool ShowForecast;

        public string TruncatedHeader = "";
        public string MainTemp = "";
        public IReadOnlyList<string> Metrics = [];
        public float[] MetricWidths = [];
        public string[] ForecastRanges = [];
        public string[] DailyHighLows = [];
        public string[] HourlyTemps = [];
    }
}

