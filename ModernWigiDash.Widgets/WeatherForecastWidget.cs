using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("weather_forecast", "Weather Forecast", Description = "Displays live real-time weather, hourly/daily forecasts, metrics, and custom layouts via Open-Meteo API. Supports city names, ZIP/postal codes, and coordinates.", Author = "ModernWigiDash", Version = "2.0.0", Category = "Social & Visual", DefaultGridSize = GridSizePreset.Size5x4)]
public class WeatherForecastWidget : ModernWidgetBase
{
    private const float DesignWidth = 406f;
    private const float DesignHeight = 296f;

    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size5x4.ToSize();
    public override SKSize MinimumSize => new SKSize(200, 160);

    [WidgetProperty("Location Type", WidgetPropertyType.Choice, "City name, ZIP code, or lat,lon pair", "Fixed Location", "Fixed Location")]
    public string LocationType { get; set; } = "Fixed Location";

    [WidgetProperty("Location", WidgetPropertyType.Text, "City name, ZIP/postal code, or lat,lon (e.g. 40.71,-74.00)", "New York")]
    public string Location { get; set; } = "New York";

    [WidgetProperty("Custom Label", WidgetPropertyType.Text, "Custom title display name override", "")]
    public string CustomLabel { get; set; } = "";

    [WidgetProperty("Unit System", WidgetPropertyType.Choice, "Temperature & speed units", "Fahrenheit (°F, mph)", "Fahrenheit (°F, mph)", "Celsius (°C, km/h)", "Celsius (°C, mph)", "Celsius (°C, m/s)", "Kelvin (K, m/s)")]
    public string UnitSystem { get; set; } = "Fahrenheit (°F, mph)";

    [WidgetProperty("Layout Mode", WidgetPropertyType.Choice, "Display view style", "Detailed", "Detailed", "Daily Forecast", "Hourly Forecast", "Current Only", "Compact")]
    public string LayoutMode { get; set; } = "Detailed";

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

    private readonly WeatherClient _client;

    public WeatherForecastWidget()
    {
        _client = new WeatherClient(CacheDir, $"weather_{InstanceId}.json", logError: (message, exception) => Context?.LogError(message, exception));
    }

    /// <summary>Test seam: injectable clock for fetch throttling and cache timestamps (forwards to the client).</summary>
    internal TimeProvider Clock { get => _client.Clock; set => _client.Clock = value; }

    /// <summary>Test seam: substitute HTTP transport for fetch tests (forwards to the client).</summary>
    internal HttpClient? TestHttpClient { get => _client.TestHttpClient; set => _client.TestHttpClient = value; }

    private double _currentTempC = 25.0; // 77°F default
    private double _feelsLikeC = 22.2;  // 72°F default
    private double _humidity = 87.0;
    private double _windSpeedKmH = 16.1; // 10 mph default
    private int _weatherCode = 51;      // Drizzle default
    private double _highTempC = 26.6;   // 80°F default
    private double _lowTempC = 20.5;    // 69°F default

    internal readonly List<DailyForecastItem> _dailyForecasts = [];
    internal readonly List<HourlyForecastItem> _hourlyForecasts = [];
    private readonly Lock _forecastGate = new();
    private IReadOnlyList<DailyForecastItem> _dailyForecastSnapshot = [];
    private IReadOnlyList<HourlyForecastItem> _hourlyForecastSnapshot = [];
    private SKRect _lastBounds;

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

    private void WeatherRefreshTick() => _ = FetchLiveWeatherAsync();

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
        if (propertyName is nameof(Location) or nameof(Latitude) or nameof(Longitude))
        {
            _client.InvalidateLocation();
            _ = FetchLiveWeatherAsync(force: true);
        }
        base.OnPropertyChanged(propertyName, newValue);
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        // Kick the fetch only when the static-snapshot rule allows; the
        // client's atomic claim decides throttling/in-flight (a check-then-set
        // here would race the 15-min refresh loop).
        if (!IsStaticSnapshotBlocking)
        {
            _ = FetchLiveWeatherAsync();
        }

        _lastBounds = bounds;

        // Snapshot the forecast lists so the fetch thread's swaps never mutate
        // a list mid-render.
        lock (_forecastGate)
        {
            _dailyForecastSnapshot = _dailyForecasts.ToArray();
            _hourlyForecastSnapshot = _hourlyForecasts.ToArray();
        }

        SKColor accentColor = ColorOf(AccentColorHex, new SKColor(255, 205, 133));
        SKColor textPrimary = SKColors.White;
        SKColor textSecondary = SKColors.White;

        float sx = bounds.Width / DesignWidth;
        float sy = bounds.Height / DesignHeight;
        float s = Math.Min(sx, sy);
        float pad = Math.Clamp(14f * s, 8f, 32f);
        float headerHeight = Math.Clamp(44f * sy, 24f, 90f);

        // Prominent Location Name Header
        string cityRaw = string.IsNullOrWhiteSpace(CustomLabel) ? _client.ResolvedCityName : CustomLabel;
        string headerDisplay = cityRaw.ToUpperInvariant();
        float locationFontSize = Math.Clamp(24f * s, 12f, 44f);
        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, locationFontSize);
        using var titlePaint = new SKPaint { Color = textPrimary, IsAntialias = true };

        float headerTextY = bounds.Top + headerHeight * 0.65f;

        // Auto-truncate city name to guarantee header text fits without overlapping badge
        var (tempUnit, speedUnit) = WeatherClient.ParseUnitSystem(UnitSystem);
        float badgeWidth = Math.Clamp(54f * s, 30f, 100f);
        float badgeHeight = Math.Clamp(26f * sy, 16f, 50f);
        float maxTitleW = Math.Max(30f, bounds.Width - pad * 2f - badgeWidth);

        string truncatedHeader = TextRenderHelper.TruncateText(headerDisplay, titleFont, maxTitleW);
        canvas.DrawTextWithFallback(truncatedHeader, bounds.Left + pad, headerTextY, titleFont, titlePaint);

        // Styled Unit Toggle Badge [°F] / [°C] (No background card)
        SKRect badgeRect = new(bounds.Right - pad - badgeWidth, bounds.Top + (headerHeight - badgeHeight) / 2f, bounds.Right - pad, bounds.Top + (headerHeight + badgeHeight) / 2f);

        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(17f * s, 10f, 30f));
        using var unitPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float uW = FontHelper.MeasureTextWithFallback(tempUnit, unitFont);
        canvas.DrawTextWithFallback(tempUnit, badgeRect.MidX - uW / 2f, badgeRect.MidY + 4.5f * s, unitFont, unitPaint);

        // Content Area Bounds
        SKRect contentBounds = new(bounds.Left + pad, bounds.Top + headerHeight + 6f * sy, bounds.Right - pad, bounds.Bottom - pad);

        switch (LayoutMode)
        {
            case "Daily Forecast":
                RenderDailyForecast(canvas, contentBounds, accentColor, textPrimary, textSecondary, tempUnit, sx, sy);
                break;
            case "Hourly Forecast":
                RenderHourlyForecast(canvas, contentBounds, accentColor, textSecondary, tempUnit, sx, sy);
                break;
            case "Current Only":
                RenderCurrentOnly(canvas, contentBounds, accentColor, textPrimary, tempUnit, sx, sy);
                break;
            case "Compact":
                RenderCompact(canvas, contentBounds, textPrimary, tempUnit, sx, sy);
                break;
            default:
                RenderDetailed(canvas, contentBounds, accentColor, textPrimary, textSecondary, tempUnit, speedUnit, sx, sy);
                break;
        }
    }

    private void RenderDetailed(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, string tempUnit, string speedUnit, float sx, float sy)
    {
        var (icon, desc) = WeatherClient.MapWmoCode(_weatherCode);
        float s = Math.Min(sx, sy);
        float w = bounds.Width;
        float h = bounds.Height;

        // Show forecast strip only if container height is at least 150px physical units
        bool hasForecast = ShowForecast && _dailyForecastSnapshot.Count > 0 && h >= 150f;
        float forecastH = hasForecast ? Math.Clamp(80f * sy, 45f, 160f) : 0f;

        List<string> metrics = [];
        if (ShowFeelsLike) metrics.Add($"Feels: {WeatherClient.FormatTemp(_feelsLikeC, tempUnit, true)}");
        if (ShowHumidity) metrics.Add($"Humidity: {_humidity:F0}%");
        if (ShowWind) metrics.Add($"Wind: {WeatherClient.FormatSpeed(_windSpeedKmH, speedUnit)}");
        if (ShowHighLow) metrics.Add($"H:{WeatherClient.FormatTemp(_highTempC, tempUnit, true)} L:{WeatherClient.FormatTemp(_lowTempC, tempUnit, true)}");

        // Show metrics pill strip only if container height is at least 150px physical units
        bool hasMetrics = metrics.Count > 0 && h >= 150f;
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

        string mainTempStr = WeatherClient.FormatTemp(_currentTempC, tempUnit);
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

        if (textStackTotalH > heroHeight * 0.85f)
        {
            float fitScale = (heroHeight * 0.85f) / textStackTotalH;
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

        RenderMetricPills(canvas, bounds, metrics, hasMetrics, metricsH, heroBottom, textSecondary, sx, sy);
        RenderForecastStrip(canvas, bounds, hasForecast, forecastH, accentColor, textPrimary, textSecondary, tempUnit, sx, sy);
    }

    private void RenderMetricPills(SKCanvas canvas, SKRect bounds, List<string> metrics, bool hasMetrics, float metricsH, float heroBottom, SKColor textSecondary, float sx, float sy)
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
        float[] metricWidths = new float[metrics.Count];
        for (int i = 0; i < metrics.Count; i++)
        {
            metricWidths[i] = metricFont.MeasureText(metrics[i]) + pillPadX * 2;
            totalPillsW += metricWidths[i];
        }
        totalPillsW += (metrics.Count - 1) * pillGap;

        // If pills exceed bounds width, scale down metric font size to fit inside card
        if (totalPillsW > w)
        {
            float metricScale = Math.Max(0.6f, w / totalPillsW);
            metricFontSize = Math.Max(7f, metricFontSize * metricScale);
            metricFont.Size = metricFontSize;
            pillPadX *= metricScale;
            pillGap *= metricScale;

            totalPillsW = 0f;
            for (int i = 0; i < metrics.Count; i++)
            {
                metricWidths[i] = metricFont.MeasureText(metrics[i]) + pillPadX * 2;
                totalPillsW += metricWidths[i];
            }
            totalPillsW += (metrics.Count - 1) * pillGap;
        }

        float pillStartX = bounds.MidX - totalPillsW / 2f;
        for (int i = 0; i < metrics.Count; i++)
        {
            SKRect pillRect = new(pillStartX, pillY, pillStartX + metricWidths[i], pillY + pillHeight);
            using var pillBorder = new SKPaint { Color = new SKColor(255, 255, 255, 22), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f * s, 1f), IsAntialias = true };
            canvas.DrawRoundRect(pillRect, 8f * s, 8f * s, pillBorder);

            using var metricPaint = new SKPaint { Color = textSecondary, IsAntialias = true };
            metricFont.GetFontMetrics(out var mMetrics);
            float mBaseline = pillRect.MidY - (mMetrics.Ascent + mMetrics.Descent) / 2f;
            canvas.DrawTextWithFallback(metrics[i], pillRect.MidX, mBaseline, metricFont, metricPaint, SKTextAlign.Center);
            pillStartX += metricWidths[i] + pillGap;
        }
    }

    private void RenderForecastStrip(SKCanvas canvas, SKRect bounds, bool hasForecast, float forecastH, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, string tempUnit, float sx, float sy)
    {
        if (!hasForecast) return;

        float s = Math.Min(sx, sy);
        float w = bounds.Width;
        int count = Math.Min(_dailyForecastSnapshot.Count, 5);
        float stripY = bounds.Bottom - forecastH;
        SKRect stripBounds = new(bounds.Left, stripY, bounds.Right, bounds.Bottom);

        using var stripBorder = new SKPaint { Color = new SKColor(255, 255, 255, 18), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f * s, 1f), IsAntialias = true };
        canvas.DrawRoundRect(stripBounds, 12f * s, 12f * s, stripBorder);

        float colWidth = w / count;
        float dayFontSize = Math.Clamp(14f * s, 8f, 24f);
        float dayIconFontSize = Math.Clamp(22f * s, 10f, 48f);
        float rangeFontSize = Math.Clamp(12f * s, 7f, 22f);

        for (int i = 0; i < count; i++)
        {
            var day = _dailyForecastSnapshot[i];
            var (dayIcon, _) = WeatherClient.MapWmoCode(day.WeatherCode);
            float colCx = bounds.Left + (i + 0.5f) * colWidth;

            var dayFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, dayFontSize);
            using var dayPaint = new SKPaint { Color = i == 0 ? accentColor : textPrimary, IsAntialias = true };
            float dayY = stripY + Math.Clamp(18f * s, 10f, 36f);

            dayFont.MeasureText(day.DayName, out var dayBounds);
            float dayX = colCx - (dayBounds.Left + dayBounds.Width / 2f);
            canvas.DrawTextWithFallback(day.DayName, dayX, dayY, dayFont, dayPaint);

            string rangeStr = $"{WeatherClient.FormatTemp(day.MaxTempC, tempUnit, true)} / {WeatherClient.FormatTemp(day.MinTempC, tempUnit, true)}";
            var rangeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, rangeFontSize);
            using var rangePaint = new SKPaint { Color = textSecondary, IsAntialias = true };
            float rangeY = stripBounds.Bottom - Math.Clamp(10f * s, 5f, 20f);

            rangeFont.MeasureText(rangeStr, out var rangeBounds);
            float rangeX = colCx - (rangeBounds.Left + rangeBounds.Width / 2f);
            canvas.DrawTextWithFallback(rangeStr, rangeX, rangeY, rangeFont, rangePaint);

            var dayIconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Normal, dayIconFontSize);
            using var dayIconPaint = new SKPaint { IsAntialias = true };

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

            canvas.DrawTextWithFallback(dayIcon, iconX, dayIconBaseline, dayIconFont, dayIconPaint);
        }
    }

    private void RenderDailyForecast(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, SKColor textSecondary, string tempUnit, float sx, float sy)
    {
        int count = Math.Min(_dailyForecastSnapshot.Count, 5);
        if (count == 0) return;

        float rowHeight = bounds.Height / count;
        float s = Math.Min(sx, sy);

        for (int i = 0; i < count; i++)
        {
            var day = _dailyForecastSnapshot[i];
            float y = bounds.Top + (i * rowHeight);
            SKRect rowRect = new(bounds.Left, y + 2, bounds.Right, y + rowHeight - 2);

            using var rowBg = new SKPaint { Color = new SKColor(22, 26, 40, 180), IsAntialias = true };
            using var rowBorder = new SKPaint { Color = new SKColor(255, 255, 255, 15), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawRoundRect(rowRect, 8f * s, 8f * s, rowBg);
            canvas.DrawRoundRect(rowRect, 8f * s, 8f * s, rowBorder);

            var (icon, desc) = WeatherClient.MapWmoCode(day.WeatherCode);

            var dayFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(13f * s, 9f, 18f));
            using var dayPaint = new SKPaint { Color = i == 0 ? accentColor : textPrimary, IsAntialias = true };
            canvas.DrawTextWithFallback(day.DayName, rowRect.Left + 12f * sx, rowRect.MidY + 5f * sy, dayFont, dayPaint);

            var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Normal, Math.Clamp(16f * s, 10f, 22f));
            using var iconPaint = new SKPaint { IsAntialias = true };
            canvas.DrawTextWithFallback(icon, rowRect.Left + 80f * sx, rowRect.MidY + 6f * sy, iconFont, iconPaint);

            var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Clamp(11f * s, 8f, 15f));
            using var descPaint = new SKPaint { Color = textSecondary, IsAntialias = true };
            canvas.DrawTextWithFallback(desc, rowRect.Left + 110f * sx, rowRect.MidY + 4f * sy, descFont, descPaint);

            string highLowStr = $"High: {WeatherClient.FormatTemp(day.MaxTempC, tempUnit)}  Low: {WeatherClient.FormatTemp(day.MinTempC, tempUnit)}";
            var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(12f * s, 8f, 16f));
            using var tempPaint = new SKPaint { Color = accentColor, IsAntialias = true };
            canvas.DrawTextWithFallback(highLowStr, rowRect.Right - FontHelper.MeasureTextWithFallback(highLowStr, tempFont) - 12f * sx, rowRect.MidY + 4f * sy, tempFont, tempPaint);
        }
    }

    private void RenderHourlyForecast(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textSecondary, string tempUnit, float sx, float sy)
    {
        int count = Math.Min(_hourlyForecastSnapshot.Count, 6);
        if (count == 0) return;

        float itemWidth = bounds.Width / count;
        float s = Math.Min(sx, sy);

        for (int i = 0; i < count; i++)
        {
            var item = _hourlyForecastSnapshot[i];
            float x = bounds.Left + (i * itemWidth);
            SKRect colRect = new(x + 2, bounds.Top + 4, x + itemWidth - 2, bounds.Bottom - 4);

            using var colBg = new SKPaint { Color = new SKColor(22, 26, 40, 180), IsAntialias = true };
            using var colBorder = new SKPaint { Color = new SKColor(255, 255, 255, 15), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
            canvas.DrawRoundRect(colRect, 8f * s, 8f * s, colBg);
            canvas.DrawRoundRect(colRect, 8f * s, 8f * s, colBorder);

            var (icon, _) = WeatherClient.MapWmoCode(item.WeatherCode);

            var timeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(11f * s, 8f, 15f));
            using var timePaint = new SKPaint { Color = textSecondary, IsAntialias = true };
            canvas.DrawTextWithFallback(item.TimeLabel, colRect.MidX - (FontHelper.MeasureTextWithFallback(item.TimeLabel, timeFont) / 2f), colRect.Top + 22f * sy, timeFont, timePaint);

            var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Normal, Math.Clamp(20f * s, 12f, 28f));
            using var iconPaint = new SKPaint { IsAntialias = true };
            canvas.DrawTextWithFallback(icon, colRect.MidX - 12f * sx, colRect.MidY + 6f * sy, iconFont, iconPaint);

            string tempStr = WeatherClient.FormatTemp(item.TempC, tempUnit);
            var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(12f * s, 8f, 16f));
            using var tempPaint = new SKPaint { Color = accentColor, IsAntialias = true };
            canvas.DrawTextWithFallback(tempStr, colRect.MidX - (FontHelper.MeasureTextWithFallback(tempStr, tempFont) / 2f), colRect.Bottom - 14f * sy, tempFont, tempPaint);
        }
    }

    private void RenderCurrentOnly(SKCanvas canvas, SKRect bounds, SKColor accentColor, SKColor textPrimary, string tempUnit, float sx, float sy)
    {
        var (icon, desc) = WeatherClient.MapWmoCode(_weatherCode);
        float s = Math.Min(sx, sy);
        float midY = bounds.MidY;
        float midX = bounds.MidX;

        float iconSize = Math.Clamp(88f * s, 40f, 120f);
        float tempSize = Math.Clamp(64f * s, 28f, 84f);
        float descSize = Math.Clamp(24f * s, 12f, 32f);

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, iconSize);
        using var iconPaint = new SKPaint { IsAntialias = true };
        float iconW = iconFont.MeasureText(icon);

        string mainTempStr = WeatherClient.FormatTemp(_currentTempC, tempUnit);
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

    private void RenderCompact(SKCanvas canvas, SKRect bounds, SKColor textPrimary, string tempUnit, float sx, float sy)
    {
        var (icon, _) = WeatherClient.MapWmoCode(_weatherCode);
        float s = Math.Min(sx, sy);

        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, Math.Clamp(26f * s, 14f, 32f));
        using var iconPaint = new SKPaint { IsAntialias = true };
        canvas.DrawTextWithFallback(icon, bounds.Left, bounds.MidY + 10f * sy, iconFont, iconPaint);

        string mainTempStr = WeatherClient.FormatTemp(_currentTempC, tempUnit);
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Clamp(20f * s, 12f, 26f));
        using var tempPaint = new SKPaint { Color = textPrimary, IsAntialias = true };
        canvas.DrawTextWithFallback(mainTempStr, bounds.Left + 36f * sx, bounds.MidY + 8f * sy, tempFont, tempPaint);
    }

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType != TouchEventType.TouchUp) return;

        // Hit-test against the last rendered bounds so touches line up with the
        // drawn controls at any widget size, not just the design size.
        var b = _lastBounds.Width > 0 ? _lastBounds : new SKRect(0, 0, DefaultSize.Width, DefaultSize.Height);
        float sx = b.Width / DesignWidth;
        float sy = b.Height / DesignHeight;

        if (localPoint.Y < 44f * sy && localPoint.X > b.Width - 64f * sx)
        {
            SetProperty(nameof(UnitSystem), UnitSystem.StartsWith("Fahrenheit", StringComparison.OrdinalIgnoreCase)
                ? "Celsius (°C, km/h)"
                : "Fahrenheit (°F, mph)");
            return;
        }

        if (localPoint.Y < 44f * sy && localPoint.X < 140f * sx)
        {
            SetProperty(nameof(LayoutMode), LayoutMode switch
            {
                "Detailed" => "Daily Forecast",
                "Daily Forecast" => "Hourly Forecast",
                "Hourly Forecast" => "Current Only",
                "Current Only" => "Compact",
                _ => "Detailed"
            });
            return;
        }

        _ = FetchLiveWeatherAsync(force: true);
    }

    /// <summary>
    /// Fetches live weather through the client's atomic fetch claim — the
    /// in-flight/throttle decision is the client's, single-sourced.
    /// </summary>
    /// <summary>The static-snapshot rule, single-sourced: while a static
    /// snapshot is showing, non-forced fetches are blocked (the client's
    /// atomic claim handles throttling).</summary>
    private bool IsStaticSnapshotBlocking => StaticSnapshot && _client.LastFetchTimeUtc != DateTime.MinValue;

    internal async Task FetchLiveWeatherAsync(bool force = false)
    {
        if (IsStaticSnapshotBlocking && !force) return;

        var snapshot = await _client.FetchCurrentAsync(BuildLocation(), force, _pollCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        if (snapshot is null) return;

        ApplySnapshot(snapshot);
        Context?.RequestRender();
    }

    private WeatherLocation BuildLocation()
        => new(LocationType, Location, Latitude, Longitude, CustomLabel);

    /// <summary>
    /// Applies a fetched/cached snapshot to the render fields, keeping the
    /// "response omitted this section → keep the previous value" semantics.
    /// </summary>
    private void ApplySnapshot(WeatherSnapshot snapshot)
    {
        if (snapshot.CurrentTempC is not null) _currentTempC = snapshot.CurrentTempC.Value;
        if (snapshot.FeelsLikeC is not null) _feelsLikeC = snapshot.FeelsLikeC.Value;
        if (snapshot.Humidity is not null) _humidity = snapshot.Humidity.Value;
        if (snapshot.WindSpeedKmH is not null) _windSpeedKmH = snapshot.WindSpeedKmH.Value;
        if (snapshot.WeatherCode is not null) _weatherCode = snapshot.WeatherCode.Value;
        if (snapshot.HighTempC is not null) _highTempC = snapshot.HighTempC.Value;
        if (snapshot.LowTempC is not null) _lowTempC = snapshot.LowTempC.Value;
        if (snapshot.DailyForecasts is not null)
            lock (_forecastGate) { _dailyForecasts.Clear(); _dailyForecasts.AddRange(snapshot.DailyForecasts); }
        if (snapshot.HourlyForecasts is not null)
            lock (_forecastGate) { _hourlyForecasts.Clear(); _hourlyForecasts.AddRange(snapshot.HourlyForecasts); }
    }

    private async Task LoadCachedWeatherAsync()
    {
        var cached = await _client.LoadCacheAsync().ConfigureAwait(false);
        if (cached is not null) ApplySnapshot(cached);
    }
}
