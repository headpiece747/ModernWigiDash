using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Live weather widget: current conditions, humidity, wind, feels-like, and
/// high/low, plus the multi-day and hourly forecast strips. Presentation layer
/// driven by the deep <see cref="WeatherRuntimeEngine"/> (Candidate 4).
/// </summary>
[WidgetMetadata("weather_forecast", "Weather Forecast", Category = "Social & Visual", DefaultGridSize = GridSizePreset.Size5x4)]
public class WeatherForecastWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider, IWidgetLocationSearch, IWidgetEditorProvider
{
    /// <summary>The "Location Type" property: city name, ZIP code, or lat,lon pair.</summary>
    [WidgetProperty("Location Type", WidgetPropertyType.Choice, "City name, ZIP code, or lat,lon pair", "Fixed Location", "Fixed Location")]
    public string LocationType { get; set; } = "Fixed Location";

    /// <summary>The "Location" property: city name, ZIP/postal code, or lat,lon pair.</summary>
    [WidgetProperty("Location", WidgetPropertyType.Text, "City name, ZIP/postal code, or lat,lon (e.g. 40.71,-74.00)", "Miami, Florida")]
    public string Location { get; set; } = "Miami, Florida";

    /// <summary>The "Hide Location" property: hide the location name from the header title.</summary>
    [WidgetProperty("Hide Location", WidgetPropertyType.Boolean, "Hide the location name from the header (a custom label still shows)", false)]
    public bool HideLocation { get; set; } = false;

    /// <summary>The "Custom Label" property: the custom title display name override.</summary>
    [WidgetProperty("Custom Label", WidgetPropertyType.Text, "Custom title display name override", "")]
    public string CustomLabel { get; set; } = "";

    /// <summary>The "Unit System" property: temperature and speed units.</summary>
    [WidgetProperty("Unit System", WidgetPropertyType.Choice, "Temperature & speed units", "Fahrenheit (°F, mph)", "Fahrenheit (°F, mph)", "Celsius (°C, km/h)", "Celsius (°C, mph)", "Celsius (°C, m/s)", "Kelvin (K, m/s)")]
    public string UnitSystem { get; set; } = WeatherPresentation.DefaultUnitSystem;

    /// <summary>The "Layout Mode" property: the display view style (Detailed, Daily Forecast, Hourly Forecast, Current Only, Compact).</summary>
    [WidgetProperty("Layout Mode", WidgetPropertyType.Choice, "Display view style", WeatherLayout.DefaultLayoutMode, "Detailed", "Daily Forecast", "Hourly Forecast", "Current Only", "Compact")]
    public string LayoutMode { get; set; } = WeatherLayout.DefaultLayoutMode;

    /// <summary>The "Accent Color" property: the primary glowing accent color.</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary glowing accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Show Humidity" property: display the relative humidity metric.</summary>
    [WidgetProperty("Show Humidity", WidgetPropertyType.Boolean, "Display relative humidity metric", true)]
    public bool ShowHumidity { get; set; } = true;

    /// <summary>The "Show Wind" property: display wind speed and direction.</summary>
    [WidgetProperty("Show Wind", WidgetPropertyType.Boolean, "Display wind speed & direction", true)]
    public bool ShowWind { get; set; } = true;

    /// <summary>The "Show Feels Like" property: display the apparent temperature.</summary>
    [WidgetProperty("Show Feels Like", WidgetPropertyType.Boolean, "Display apparent temperature", true)]
    public bool ShowFeelsLike { get; set; } = true;

    /// <summary>The "Show High / Low" property: display today's max and min temperature.</summary>
    [WidgetProperty("Show High / Low", WidgetPropertyType.Boolean, "Display today's max and min temp", true)]
    public bool ShowHighLow { get; set; } = true;

    /// <summary>The "Show Forecast Strip" property: display the multi-day forecast strip in the Detailed view.</summary>
    [WidgetProperty("Show Forecast Strip", WidgetPropertyType.Boolean, "Display multi-day forecast strip in Detailed view", true)]
    public bool ShowForecast { get; set; } = true;

    /// <summary>The "Static Snapshot" property: freeze the current weather data as a static snapshot.</summary>
    [WidgetProperty("Static Snapshot", WidgetPropertyType.Boolean, "Freeze current weather data as a static snapshot", false)]
    public bool StaticSnapshot { get; set; } = false;

    /// <summary>The "Latitude" property: override latitude; leave empty to auto-resolve from Location.</summary>
    [WidgetProperty("Latitude", WidgetPropertyType.Text, "Override latitude (e.g. 40.7128). Leave empty to auto-resolve from Location.", "")]
    public string Latitude { get; set; } = "";

    /// <summary>The "Longitude" property: override longitude; leave empty to auto-resolve from Location.</summary>
    [WidgetProperty("Longitude", WidgetPropertyType.Text, "Override longitude (e.g. -74.0060). Leave empty to auto-resolve from Location.", "")]
    public string Longitude { get; set; } = "";

    /// <summary>The "Country Code" property: an optional ISO country code that disambiguates same-named cities.</summary>
    [WidgetProperty("Country Code", WidgetPropertyType.Text, "Optional ISO country code (US, DE, CA, JP...) to disambiguate same-named cities worldwide. You can also type \"City, State\" or \"City, Country\" in Location.", "")]
    public string CountryCode { get; set; } = "";

    /// <summary>
    /// Pick from the geocoder's candidates when a city name resolves ambiguously.
    /// </summary>
    [WidgetProperty("Location Match", WidgetPropertyType.Choice, "Pick the exact place when the city name is ambiguous. Leave empty for automatic.", "")]
    public string LocationMatch { get; set; } = "";

    private readonly WeatherRuntimeEngine _engine;
    private readonly WeatherWidgetRenderer _renderer = new();
    private static readonly string CacheDir = Path.Combine(AppContext.BaseDirectory, "weather_cache");
    private SKRect _lastBounds;
    internal WeatherRenderModel? _renderModel;

    /// <summary>
    /// Constructs the weather forecast widget bound to its deep runtime engine.
    /// </summary>
    public WeatherForecastWidget()
    {
        _engine = new WeatherRuntimeEngine(
            cacheDir: CacheDir,
            instanceIdProvider: () => InstanceId,
            locationProvider: BuildLocation,
            isStaticSnapshotProvider: () => StaticSnapshot,
            onRepaint: () => Context?.RequestRender(),
            onInspectorRefresh: () => Context?.RequestInspectorRefresh(),
            onWriteback: label => SetProperty(nameof(Location), label),
            logError: (message, exception) => Context?.LogError(message, exception),
            logInfo: message => Context?.LogInfo(message));
    }

    /// <inheritdoc/>
    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (!string.Equals(propertyName, nameof(LocationMatch), StringComparison.Ordinal)) return [];

        var candidates = _engine.Candidates;
        if (candidates.Count == 0) return [];

        return
        [
            new WidgetPropertyOption("", "Automatic (by ranking)"),
            .. candidates.Select(c => new WidgetPropertyOption(c.Query, c.Label))
        ];
    }

    /// <summary>Geocodes the location search query (forwards to the engine's city search).</summary>
    public Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct)
        => _engine.SearchCitiesAsync(query, ct);

    /// <summary>Commits a pick from the search results: writes the candidate's label into Location.</summary>
    public void CommitPick(GeocodeCandidate candidate)
    {
        SetProperty(nameof(Location), candidate.Label);
    }

    /// <summary>The resolved location's population (null when none).</summary>
    public double? CurrentPopulation => _engine.CurrentPopulation;

    /// <inheritdoc/>
    public EditorKind? GetEditorKind(PropertyInfo property)
        => string.Equals(property.Name, nameof(Location), StringComparison.Ordinal) ? EditorKind.LocationSearch : null;

    /// <inheritdoc/>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        _engine.Start();
    }

    /// <inheritdoc/>
    public override ValueTask DisposeAsync()
    {
        _engine.Dispose();
        _renderer.Dispose();
        return base.DisposeAsync();
    }


    /// <inheritdoc/>
    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        WeatherInvalidationKind kind = WeatherInvalidation.KindForProperty(propertyName);
        if (kind == WeatherInvalidationKind.Location && _engine.IsLocationWritebackSuppressed)
        {
            kind = WeatherInvalidationKind.None;
        }

        if (kind != WeatherInvalidationKind.None)
        {
            _engine.Invalidate(kind);
        }

        base.OnPropertyChanged(propertyName, newValue);
    }

    /// <inheritdoc/>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _engine.ApplyPendingLocationWriteback();

        SKColor accentColor = ColorOf(AccentColorHex, WidgetPalette.Accent);
        SKColor textPrimary = SKColors.White;
        SKColor textSecondary = SKColors.White;

        _engine.RequestRefresh();

        _lastBounds = bounds;

        var (sx, sy, s) = WeatherLayout.Scale(bounds);
        var header = WeatherLayout.ComputeHeader(bounds, s, sy);
        var (tempUnit, _) = WeatherPresentation.ParseUnitSystem(UnitSystem);

        var (buildInputs, lastSuccessFetchTime) = _engine.DisplayState.CaptureRenderView(
            bounds, header, s,
            LayoutMode, UnitSystem, CustomLabel, HideLocation,
            ShowFeelsLike, ShowHumidity, ShowWind, ShowHighLow, ShowForecast,
            Location);

        var model = WeatherRenderModelFactory.Resolve(_renderModel, buildInputs);
        if (!ReferenceEquals(_renderModel, model)) _renderModel = model;

        SKRect contentBounds = _renderer.RenderHeader(
            canvas, bounds, header, s, sy, model, tempUnit,
            IsFetchInFlight, lastSuccessFetchTime, Clock.GetUtcNow().UtcDateTime,
            textPrimary);

        switch (WeatherLayout.ParseMode(LayoutMode))
        {
            case WeatherLayoutMode.DailyForecast:
                _renderer.RenderDailyForecast(canvas, contentBounds, accentColor, textPrimary, textSecondary, sx, sy, model);
                break;
            case WeatherLayoutMode.HourlyForecast:
                _renderer.RenderHourlyForecast(canvas, contentBounds, accentColor, textSecondary, sx, sy, model);
                break;
            case WeatherLayoutMode.CurrentOnly:
                _renderer.RenderCurrentOnly(canvas, contentBounds, accentColor, textPrimary, sx, sy, model);
                break;
            case WeatherLayoutMode.Compact:
                _renderer.RenderCompact(canvas, contentBounds, textPrimary, sx, sy, model);
                break;
            default:
                _renderer.RenderDetailed(canvas, contentBounds, accentColor, textPrimary, textSecondary, sx, sy, model);
                break;
        }
    }

    /// <inheritdoc/>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType != TouchEventType.TouchUp) return;

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
                _engine.RequestRefresh(force: true);
                return;
        }
    }

    private WeatherLocation BuildLocation()
        => new(LocationType, Location, Latitude, Longitude, CustomLabel, string.IsNullOrWhiteSpace(CountryCode) ? null : CountryCode.Trim())
        {
            LocationMatch = string.IsNullOrWhiteSpace(LocationMatch) ? null : LocationMatch.Trim()
        };

    // ── Test & Seam Forwards ────────────────────────────────

    internal WeatherRuntimeEngine Engine => _engine;

    internal TimeProvider Clock { get => _engine.Clock; set => _engine.Clock = value; }

    internal HttpClient? TestHttpClient { get => _engine.TestHttpClient; set => _engine.TestHttpClient = value; }

    internal string ResolvedCityName => _engine.ResolvedCityName;

    internal string CacheFileName => _engine.CacheFileName;

    internal int FetchCompletedCount => _engine.FetchCompletedCount;

    internal bool IsFetchInFlight => _engine.IsFetchInFlight;

    internal WeatherSnapshotState _snapshotState => _engine.SnapshotState;

    internal CancellationTokenSource? _pollCts
    {
        get => _engine.PollCts;
        set => _engine.PollCts = value;
    }

    internal void RequestRefresh(bool force = false) => _engine.RequestRefresh(force);

    internal void SuppressLocationWriteback() => _engine.SuppressLocationWriteback();

    internal Task<WeatherFetchFlowOutcome> FetchLiveWeatherAsync(bool force = false)
        => _engine.FetchLiveWeatherAsync(force);

    internal void ApplyPendingLocationWriteback() => _engine.ApplyPendingLocationWriteback();

    internal Func<WeatherLocation, CancellationToken, Task<WeatherSnapshot?>>? CacheLoadOverride
    {
        get => _engine.CacheLoadOverride;
        set => _engine.CacheLoadOverride = value;
    }

    internal Task LoadCachedWeatherAsync(CancellationToken cancellationToken)
        => _engine.LoadCachedWeatherAsync(cancellationToken);
}
