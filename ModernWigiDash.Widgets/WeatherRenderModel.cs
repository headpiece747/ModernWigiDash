using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The render-model cache key: the data version, the bounds
/// (layout-derived font sizes), and the property snapshot that changes any
/// formatted string — every model component that can change the strings in
/// one value-semantics record (strings ordinal, bounds componentwise).
/// Built once per build; the cache hit test is a single record comparison.
/// </summary>
internal sealed record WeatherRenderModelKey(
    int DataVersion,
    SKRect Bounds,
    string LayoutMode,
    string UnitSystem,
    string CustomLabel,
    string ResolvedCity,
    bool ShowFeelsLike,
    bool ShowHumidity,
    bool ShowWind,
    bool ShowHighLow,
    bool ShowForecast,
    int CandidateCount);

/// <summary>
/// The cached render model: every formatted string the five layout modes
/// draw, plus the data slices the draw paths need, recomputed only when its
/// key components change. The <see cref="Key"/> covers everything that can
/// change the strings — the data version, the bounds (layout-derived font
/// sizes), and the property snapshot (mode, unit system, custom label,
/// visibility toggles).
/// </summary>
internal sealed class WeatherRenderModel
{
    /// <summary>The cache identity the model was built under; null on a
    /// model that never went through the widget's build (so it can never be
    /// a cache hit).</summary>
    public WeatherRenderModelKey? Key;
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
    public int CandidateCount;

    public int WeatherCode;
    public bool IsDay = true;
    public DailyForecastItem[] Daily = [];
    public HourlyForecastItem[] Hourly = [];
    public WeatherDisplay Display = new("", [], [], [], []);
    public string TruncatedHeader = "";
    public string? SubtitleText;
    public float[] MetricWidths = [];
}
