namespace ModernWigiDash.Widgets;

/// <summary>
/// The render-model cache key: the data version, the bounds
/// (layout-derived font sizes), and the property snapshot that changes any
/// formatted string — every model component that can change the strings in
/// one value-semantics record (strings ordinal, bounds componentwise). The
/// location's emptiness rides here, not in the data version: it changes the
/// subtitle's guidance line while no fetch applies (a failed or pending
/// fetch never bumps the version), so a location edit with no data must
/// still rebuild. HideLocation rides here too: it changes the header title
/// (the resolved city, the unknown-location placeholder, and the neutral
/// fallback alike render nothing while a custom label still shows) without
/// any fetch or data bump. Built once per build; the cache hit test is a
/// single record comparison.
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
    bool HideLocation,
    int CandidateCount,
    bool LocationSet = false);

/// <summary>
/// The cached render model: every formatted string the five layout modes
/// draw, plus the data slices the draw paths need, recomputed only when its
/// key components change. The <see cref="Key"/> covers everything that can
/// change the strings — the data version, the bounds (layout-derived font
/// sizes), and the property snapshot (mode, unit system, custom label,
/// visibility toggles, the location's emptiness).
/// </summary>
internal sealed class WeatherRenderModel
{
    /// <summary>The cache identity the model was built under; null on a
    /// model that never went through the widget's build (so it can never be
    /// a cache hit). The key is the model's SINGLE identity — the property
    /// snapshot the draw paths read (e.g. ShowForecast) comes from here,
    /// never from a copy: a copy could drift by a forgotten factory line,
    /// the record cannot.</summary>
    public WeatherRenderModelKey? Key;

    public int WeatherCode;
    public bool IsDay = true;
    public DailyForecastItem[] Daily = [];
    public HourlyForecastItem[] Hourly = [];
    public WeatherDisplay Display = new("", [], [], [], []);
    public string TruncatedHeader = "";
    public string? SubtitleText;
    public float[] MetricWidths = [];
}
