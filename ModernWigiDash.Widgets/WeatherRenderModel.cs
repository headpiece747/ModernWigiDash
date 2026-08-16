using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The cached render model: every formatted string the five layout modes
/// draw, plus the data slices the draw paths need, recomputed only when its
/// key components change. The key covers everything that can change the
/// strings — the data version, the bounds (layout-derived font sizes), and
/// the property snapshot (mode, unit system, custom label, visibility
/// toggles).
/// </summary>
internal sealed class WeatherRenderModel
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

    public int WeatherCode;
    public DailyForecastItem[] Daily = [];
    public HourlyForecastItem[] Hourly = [];
    public WeatherDisplay Display = new("", [], [], [], []);
    public string TruncatedHeader = "";
    public float[] MetricWidths = [];
}
