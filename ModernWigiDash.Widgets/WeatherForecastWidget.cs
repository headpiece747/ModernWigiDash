using System.Reflection;
using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("weather_forecast", "Weather Forecast", Category = "Social & Visual", DefaultGridSize = GridSizePreset.Size5x4)]
public class WeatherForecastWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider, IWidgetLocationSearch, IWidgetEditorProvider
{
    [WidgetProperty("Location Type", WidgetPropertyType.Choice, "City name, ZIP code, or lat,lon pair", "Fixed Location", "Fixed Location")]
    public string LocationType { get; set; } = "Fixed Location";

    // The default carries a state suffix on purpose: a bare city name (the old
    // "New York") ties across many exact-name geocoder candidates, so the
    // ambiguity gate leaves it unresolved and the widget starts blank.
    // "Miami, Florida" is the single unique top scorer — it fetches out of the
    // box on a fresh profile.
    [WidgetProperty("Location", WidgetPropertyType.Text, "City name, ZIP/postal code, or lat,lon (e.g. 40.71,-74.00)", "Miami, Florida")]
    public string Location { get; set; } = "Miami, Florida";

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
        if (!string.Equals(propertyName, nameof(LocationMatch), StringComparison.Ordinal)) return [];

        // Empty candidates: no dropdown yet (the geocode may not have run).
        if (_identity.Candidates.Count == 0) return [];

        // The empty "Automatic (by ranking)" entry lets a pick be cleared.
        return
        [
            new WidgetPropertyOption("", "Automatic (by ranking)"),
            .. _identity.Candidates.Select(c => new WidgetPropertyOption(c.Query, c.Label))
        ];
    }

    private readonly WeatherClient _client;
    private readonly WeatherFetchFlow _flow;

    // The widget's own copy of the resolved identity, taken from the Fetched
    // outcome (or the boot cache load): the client reports the identity once
    // per fetch, and the widget owns the dropdown, population, header title,
    // and the render-model cache key from it, so it never re-reads the
    // client's resolution state on the render path. The identity module owns
    // the state transitions (Apply / Invalidate* / pending write-back); the
    // widget keeps only the gate discipline (every mutation runs under
    // _forecastGate) and the UI-thread flush.
    private readonly WeatherResolvedIdentity _identity = new(WeatherFetchControl.UnknownLocationLabel);

    // -- IWidgetLocationSearch ------------------------------------------------

    public Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct)
        => _client.SearchCitiesAsync(query, ct);

    public void CommitPick(GeocodeCandidate candidate)
    {
        // The name is the truth: a pick writes only the label. Latitude/Longitude
        // stay manual-only — the label resolves deterministically (multi-component
        // suffix matching).
        SetProperty(nameof(Location), candidate.Label);
    }

    public double? CurrentPopulation => _identity.Population > 0 ? _identity.Population : null;

    // -- IWidgetEditorProvider ------------------------------------------------

    public EditorKind? GetEditorKind(PropertyInfo property)
        => string.Equals(property.Name, nameof(Location), StringComparison.Ordinal) ? EditorKind.LocationSearch : null;

    public WeatherForecastWidget()
    {
        // The cache name is resolved lazily from the CURRENT InstanceId, not
        // baked at construction: RehydrateWidget assigns the placed InstanceId
        // only after the widget is built, so a baked name would key every
        // load/save by a fresh never-reused GUID (the cache would never round
        // trip across restarts). Defense in depth: the profile-import boundary
        // already regenerates unsafe ids, but the name builder also refuses
        // one here so a cache file can never escape the cache directory.
        _client = new WeatherClient(CacheDir, () => $"weather_{SafeCacheToken(InstanceId)}.json", logError: (message, exception) => Context?.LogError(message, exception));
        // The fetch flow is the deep module behind the host seams: the client
        // (fetch/load legs, throttle truth, the discarded-load rollback) and
        // the identity module are the cluster's real modules; these seams
        // carry the host concerns across its interface — the property
        // coercion, the gate around the display state (the apply and the
        // version read run under _forecastGate), the UI-thread write-back
        // flush's gate, and the context requests.
        _flow = new WeatherFetchFlow(
            _client,
            _identity,
            currentLocation: BuildLocation,
            applySnapshot: (snapshot, expectedVersion, identityGuard, candidates, population, resolvedName) =>
                ApplySnapshot(snapshot, expectedVersion, identityGuard, candidates, population, resolvedName),
            dataVersion: () =>
            {
                lock (_forecastGate) { return _snapshotState.DataVersion; }
            },
            isStaticSnapshot: () => StaticSnapshot,
            runToken: () => _pollCts?.Token ?? CancellationToken.None,
            setPendingWritebackIfCurrent: (identityGuard, value) =>
            {
                lock (_forecastGate)
                {
                    if (identityGuard())
                    {
                        _identity.SetPendingWriteback(value);
                    }
                }
            },
            requestRender: () => Context?.RequestRender(),
            requestInspectorRefresh: () => Context?.RequestInspectorRefresh());
    }

    /// <summary>Test seam: injectable clock for fetch throttling and cache timestamps (forwards to the client).</summary>
    internal TimeProvider Clock { get => _client.Clock; set => _client.Clock = value; }

    /// <summary>Test seam: substitute HTTP transport for fetch tests (forwards to the client).</summary>
    internal HttpClient? TestHttpClient { get => _client.TestHttpClient; set => _client.TestHttpClient = value; }

    /// <summary>The last resolved display name (test/UI seam: the widget's own
    /// copy of the identity the Fetched outcome reported).</summary>
    internal string ResolvedCityName => _identity.CityName;

    /// <summary>The client's cache file name as currently resolved (test seam:
    /// pins the placed-InstanceId keying — the name must follow the InstanceId
    /// assigned by rehydration, never the construction-time default GUID).</summary>
    internal string CacheFileName => _client.CacheFileName;

    /// <summary>The InstanceId is a cache-file key; a foreign value with path
    /// segments must never reach the name builder (defense in depth behind the
    /// import boundary's regeneration rule). The fallback is a PER-INSTANCE
    /// token — a shared literal would collide two widgets' cache files if the
    /// guard were ever reached by more than one widget.</summary>
    private readonly string _safeCacheFallbackToken = Guid.NewGuid().ToString();

    private string SafeCacheToken(string? instanceId)
        => ProfileImportSanitizer.IsSafeInstanceId(instanceId) ? instanceId! : _safeCacheFallbackToken;

    /// <summary>Completed-fetch count (test seam: wait on fetch completion, not call start).</summary>
    internal int FetchCompletedCount => _client.FetchCompletedCount;

    // The snapshot display state — the 7 weather scalars, the 2 forecast
    // lists, and the 2 versions — as one immutable record. The apply policy
    // module merges a fetched or cached snapshot into it; the widget swaps
    // the result in under the forecast gate, so a torn write can never be
    // observed. The record's defaults are the pre-fetch placeholder scene.
    // Internal: the widget tests read the forecast lists through it.
    internal WeatherSnapshotState _snapshotState = new();

    // The per-mode draw paths live in the renderer (WeatherWidgetRenderer),
    // which owns the card/pill/row paints — one shared pair behind every
    // card, colors swapped via Paint.Color mutation (hoisted out of the
    // per-card loops). The header's two paints (title, unit badge) follow the
    // same hoisted pattern here — the 30 FPS render path allocates no paints.
    private readonly WeatherWidgetRenderer _renderer = new();
    private readonly SKPaint _titlePaint = new() { IsAntialias = true };
    private readonly SKPaint _unitPaint = new() { IsAntialias = true };

    private readonly Lock _forecastGate = new();
    private IReadOnlyList<DailyForecastItem> _dailyForecastSnapshot = [];
    private IReadOnlyList<HourlyForecastItem> _hourlyForecastSnapshot = [];
    private int _renderedForecastVersion = -1;
    private SKRect _lastBounds;

    // The render-model cache: every formatted string the draw paths need is
    // rebuilt only when (data version, bounds, property snapshot) changes —
    // weather data moves at most every 15 minutes, so the static scene
    // allocates nothing on the 30 FPS render path.
    /// <summary>The cached render model (internal test seam: the invalidation
    /// matrix pins which keys force a rebuild).</summary>
    internal WeatherRenderModel? _renderModel;

    private static readonly string CacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "weather_cache");
    private PollLoop? _refreshPoll;
    /// <summary>The fetch-cancellation CTS (internal test seam: direct-drive
    /// tests inject a cancelled token to pin the silent-teardown path).</summary>
    internal CancellationTokenSource? _pollCts;

    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        // The poll CTS is created before the boot cache load so the load can
        // take the same teardown token every other fetch leg gets.
        _pollCts = new CancellationTokenSource();
        _ = LoadCachedWeatherAsync(_pollCts.Token);
        // The refresh loop rides the repo's one loop shape at the client's
        // fetch-window cadence — the one cadence constant (visible pages are
        // driven by the render kick at the same window; the loop is the sole
        // driver for hidden pages, whose reveal-kick then refreshes anyway).
        // The old code used the last raw System.Threading.Timer: fire-and-
        // forget async callback, no readiness guard, no failure logging.
        _refreshPoll = new PollLoop(
            "WEATHER", WeatherFetchControl.FetchWindow, () => true,
            WeatherRefreshTick, () => { }, msg => Context?.LogInfo(msg));
        _refreshPoll.Start();
        // The boot fetch: InitializeAsync runs BEFORE the profile applies
        // this widget's properties (RehydrateWidget), so this fetch uses the
        // pre-hydration default location. That is deliberate:
        //   - hidden-page widgets (fresh starter profiles) have no render
        //     kick and no hydration kick — the boot fetch is their only
        //     immediate driver (the poll tick comes 5 minutes later)
        //   - the identity guard below drops any result whose location no
        //     longer matches when it returns, so the pre-hydration fetch can
        //     never DISPLAY the wrong city — at most it transiently writes
        //     the default's cache, which the hydration kick overwrites
        _ = FetchLiveWeatherAsync();
    }

    private void WeatherRefreshTick() => RequestRefresh();

    /// <summary>
    /// The single "fetch if due" entry for every cadence source: the refresh
    /// PollLoop, the per-frame render kick, the touch refresh, and the
    /// edit-time force paths all call this. The gate (static-snapshot rule +
    /// the client's throttle window) is applied ONCE inside the flow module —
    /// this host method neither re-derives the policy nor reads the client's
    /// throttle state; the client's atomic in-flight claim remains the
    /// authority (see <see cref="WeatherClient.FetchCurrentAsync"/>).
    /// </summary>
    private void RequestRefresh(bool force = false)
    {
        if (!_flow.CanFetch(force)) return;
        _ = _flow.RunFetchAsync(force);
    }

    public override async ValueTask DisposeAsync()
    {
        _refreshPoll?.Dispose();
        if (_pollCts != null)
        {
            await _pollCts.CancelAsync().ConfigureAwait(false);
            _pollCts.Dispose();
        }
        // The hoisted SKPaints wrap native Skia handles — release them like
        // every other widget's DisposeAsync (profile reloads / edits recreate
        // widgets, and finalizer-driven reclamation would accumulate).
        _renderer.Dispose();
        _titlePaint.Dispose();
        _unitPaint.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        // The drop granularity is the rule's decision (WeatherInvalidation):
        // a Location Match pick keeps the candidates it was offered from,
        // every other resolution input voids the whole identity (a stale pick
        // can never win). The two twins (the client's fetch control and the
        // widget's resolved identity) are paired PER KIND — the identity
        // transitions run under the SAME gate ApplySnapshot's guarded apply
        // takes, so the clear is atomic against an in-flight fetch's
        // assignment (either the assignment lands before the clear and is
        // erased, or the guard re-reads the new location and the assignment
        // never happens; the edit can never be resurrected over). Each
        // Invalidate* also drops a PENDING resolved-label write-back (the
        // race a completed-but-unflushed fetch leaves) — strictly stronger
        // under the gate, safe because the identity re-check is also under
        // the gate.
        WeatherInvalidationKind kind = WeatherInvalidation.KindForProperty(propertyName);
        if (kind == WeatherInvalidationKind.Location && _suppressLocationWriteback)
        {
            // The resolved-label write-back skips the forced re-fetch: the
            // label was just resolved by the fetch that wrote it, so fetching
            // again would loop (the write-back converges after one extra
            // resolution at most).
            kind = WeatherInvalidationKind.None;
        }

        switch (kind)
        {
            case WeatherInvalidationKind.Coordinates:
                lock (_forecastGate)
                {
                    _identity.InvalidateCoordinates();
                }
                _client.InvalidateCoordinates();
                RequestRefresh(force: true);
                break;
            case WeatherInvalidationKind.Location:
                lock (_forecastGate)
                {
                    _identity.InvalidateLocation();
                }
                _client.InvalidateLocation();
                RequestRefresh(force: true);
                break;
        }

        base.OnPropertyChanged(propertyName, newValue);
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        // The UI-thread flush of a deferred resolved-label write-back: the
        // fetch continuation only sets the pending field, so Context.
        // PersistProperty runs here on the UI thread (see
        // ApplyPendingLocationWriteback).
        ApplyPendingLocationWriteback();

        SKColor accentColor = ColorOf(AccentColorHex, WidgetPalette.Accent);
        SKColor textPrimary = SKColors.White;
        SKColor textSecondary = SKColors.White;

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
            if (_renderedForecastVersion != _snapshotState.ForecastVersion)
            {
                _renderedForecastVersion = _snapshotState.ForecastVersion;
                _dailyForecastSnapshot = _snapshotState.DailyForecasts.ToArray();
                _hourlyForecastSnapshot = _snapshotState.HourlyForecasts.ToArray();
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
        _titlePaint.Color = textPrimary;
        canvas.DrawTextWithFallback(model.TruncatedHeader, bounds.Left + header.Pad, header.HeaderTextY, titleFont, _titlePaint);

        // Styled Unit Toggle Badge [°F] / [°C] (No background card)
        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, WeatherLayout.BadgeFontSize(s));
        _unitPaint.Color = SKColors.White;
        float uW = FontHelper.MeasureTextWithFallback(tempUnit, unitFont);
        canvas.DrawTextWithFallback(tempUnit, header.BadgeRect.MidX - uW / 2f, header.BadgeRect.MidY + 4.5f * s, unitFont, _unitPaint);

        // Content Area Bounds
        SKRect contentBounds = new(bounds.Left + header.Pad, bounds.Top + header.HeaderHeight + 6f * sy, bounds.Right - header.Pad, bounds.Bottom - header.Pad);

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
                RequestRefresh(force: true);
                break;
        }
    }

    // The inspector-candidates stamp (refresh only when the pickable options
    // changed, never on every fetch) moved with the sequence into the flow
    // module — the host keeps only the refresh request itself.

    /// <summary>Suppresses the OnPropertyChanged fetch while the resolved label
    /// is written back after a successful resolution (the write-back must not
    /// re-fire a fetch).</summary>
    internal bool _suppressLocationWriteback;

    /// <summary>
    /// The widget's fetch entry (the direct-drive seam the tests and the edit
    /// paths use). The whole sequence — key capture, outcome verification,
    /// the post-await re-validation, drop-and-refetch routing, the guarded
    /// apply, and the write-back gating — lives in
    /// <see cref="WeatherFetchFlow"/>, so the host carries no part of the
    /// flow policy: this method is a forward.
    /// </summary>
    internal Task<WeatherFetchFlowOutcome> FetchLiveWeatherAsync(bool force = false)
        => _flow.RunFetchAsync(force);

    /// <summary>
    /// Performs the deferred resolved-label write-back on the UI thread (called
    /// by <see cref="Render"/> at 30 FPS; also an internal test seam for
    /// direct-drive tests). The pending field is cleared before the write so a
    /// re-entrant render cannot double-write; the suppression flag keeps the
    /// write's OnPropertyChanged from re-firing a fetch.
    /// </summary>
    internal void ApplyPendingLocationWriteback()
    {
        if (_identity.TakePendingWriteback() is not { } pending) return;
        if (string.IsNullOrWhiteSpace(pending) || string.Equals(pending, Location, StringComparison.Ordinal) || _suppressLocationWriteback) return;

        _suppressLocationWriteback = true;
        try
        {
            SetProperty(nameof(Location), pending);
        }
        finally
        {
            _suppressLocationWriteback = false;
        }
    }

    /// <summary>
    /// The property → identity-input coercion (blank → null, trim of the
    /// optional fields): the host owns its properties, so this shaping lives
    /// here and is passed to the flow as the <c>currentLocation</c> seam. One
    /// call site per fetch step — the flow never re-derives the shape.
    /// </summary>
    private WeatherLocation BuildLocation()
        => new(LocationType, Location, Latitude, Longitude, CustomLabel, string.IsNullOrWhiteSpace(CountryCode) ? null : CountryCode.Trim())
        {
            LocationMatch = string.IsNullOrWhiteSpace(LocationMatch) ? null : LocationMatch.Trim()
        };

    /// <summary>
    /// Applies a fetched/cached snapshot to the display state, keeping the
    /// "response omitted this section — keep the previous value" semantics.
    /// The guard (version-then-identity) and the merge (null-keeps +
    /// per-list version bump) live in <see cref="WeatherSnapshotApplyPolicy"/>;
    /// this method adds only the gate discipline: under <see cref="_forecastGate"/>
    /// it asks the guard, swaps in the merge's new state, and applies the
    /// resolved-identity copies under the SAME lock — an edit landing between
    /// the guard and the copies can no longer be resurrected over by the old
    /// identity's state (the edit cleared the copies under the same lock; the
    /// guarded assignment re-populates them only when the identity still
    /// matches — an edit that commits between the guard and the copies still
    /// clears them after the assignment lands, because the edit's clear takes
    /// the same gate). <paramref name="population"/> follows the client's
    /// no-data sentinel: when provided, 0 clears the resolved population (the
    /// fetch reported none), a non-zero value replaces it — "no data" and
    /// "keep previous" are distinguishable by null vs. provided. Returns
    /// whether the snapshot was applied.
    /// </summary>
    private bool ApplySnapshot(WeatherSnapshot snapshot, int? expectedVersion = null, Func<bool>? identityGuard = null,
        IReadOnlyList<GeocodeCandidate>? candidates = null, double? population = null, string? resolvedName = null)
    {
        lock (_forecastGate)
        {
            if (!WeatherSnapshotApplyPolicy.GuardsPass(expectedVersion, _snapshotState.DataVersion, identityGuard)) return false;
            _snapshotState = WeatherSnapshotApplyPolicy.Merge(snapshot, _snapshotState);
            _identity.Apply(candidates, population, resolvedName);
            return true;
        }
    }

    /// <summary>Test seam: replaces the client cache-load leg so the boot-race
    /// version guard is drivable deterministically (forwards to the flow's
    /// seam; defaults to the client's identity-checked load).</summary>
    internal Func<WeatherLocation, CancellationToken, Task<WeatherSnapshot?>>? CacheLoadOverride
    {
        get => _flow.CacheLoadOverride;
        set => _flow.CacheLoadOverride = value;
    }

    /// <summary>
    /// The boot cache load (InitializeAsync fires it before the boot fetch).
    /// The guarded sequence — version + identity re-checks, the atomic
    /// check-and-apply, and the discarded-load rollback of the client's
    /// committed identity state — lives in
    /// <see cref="WeatherFetchFlow"/>, so the host carries no part of it:
    /// this method is a forward.
    /// </summary>
    internal Task LoadCachedWeatherAsync(CancellationToken cancellationToken)
        => _flow.RunBootLoadAsync(cancellationToken);

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
        int weatherCode, dataVersion;
        IReadOnlyList<DailyForecastItem> daily;
        IReadOnlyList<HourlyForecastItem> hourly;
        lock (_forecastGate)
        {
            dataVersion = _snapshotState.DataVersion;
            currentTempC = _snapshotState.CurrentTempC;
            feelsLikeC = _snapshotState.FeelsLikeC;
            humidity = _snapshotState.Humidity;
            windSpeedKmH = _snapshotState.WindSpeedKmH;
            highTempC = _snapshotState.HighTempC;
            lowTempC = _snapshotState.LowTempC;
            weatherCode = _snapshotState.WeatherCode;
            daily = _dailyForecastSnapshot;
            hourly = _hourlyForecastSnapshot;
        }

        // One cache identity per build: the key record holds everything that
        // can change the formatted strings (data version, bounds, property
        // snapshot), so the hit test is a single record comparison — and the
        // key is a value snapshot (an un-rendered property change cannot
        // rewrite a cached model's key).
        var key = new WeatherRenderModelKey(
            dataVersion, bounds,
            LayoutMode, UnitSystem, CustomLabel, _identity.CityName,
            ShowFeelsLike, ShowHumidity, ShowWind, ShowHighLow, ShowForecast);

        if (_renderModel is { } cached && cached.Key == key)
        {
            return cached;
        }

        var (_, sy, s) = WeatherLayout.Scale(bounds);
        var header = WeatherLayout.ComputeHeader(bounds, s, sy);

        // The display facts (hero temp, pills, daily/hourly strings) compose
        // in WeatherPresentation; the model caches them alongside the data
        // slices the draw paths need.
        var display = WeatherPresentation.Build(new WeatherDisplayInput(
            currentTempC,
            new WeatherMetricsInput(
                ShowFeelsLike, feelsLikeC,
                ShowHumidity, humidity,
                ShowWind, windSpeedKmH,
                ShowHighLow, highTempC, lowTempC,
                tempUnit, speedUnit),
            daily,
            hourly));

        var model = new WeatherRenderModel
        {
            Key = key,
            DataVersion = dataVersion,
            Bounds = bounds,
            LayoutMode = LayoutMode,
            UnitSystem = UnitSystem,
            CustomLabel = CustomLabel,
            ResolvedCity = _identity.CityName,
            ShowFeelsLike = ShowFeelsLike,
            ShowHumidity = ShowHumidity,
            ShowWind = ShowWind,
            ShowHighLow = ShowHighLow,
            ShowForecast = ShowForecast,
            WeatherCode = weatherCode,
            Daily = daily.ToArray(),
            Hourly = hourly.ToArray(),
            Display = display,
        };

        // Auto-truncated header: the city name uppercased once per model, then
        // truncated to the same max width the draw path uses.
        string cityRaw = string.IsNullOrWhiteSpace(CustomLabel) ? _identity.CityName : CustomLabel;
        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, header.TitleFontSize);
        float maxTitleW = WeatherLayout.TitleMaxWidth(bounds.Width, header.Pad, header.BadgeRect.Width);
        model.TruncatedHeader = TextRenderHelper.TruncateText(cityRaw.ToUpperInvariant(), titleFont, maxTitleW);

        // Pill widths: measured with the pill font the draw path derives from
        // the same bounds — via the renderer's shared helper, so the model's
        // cached widths and the draw path's shrink re-measure are ONE spelling.
        model.MetricWidths = WeatherWidgetRenderer.MeasurePillWidths(
            model.Display.Metrics,
            WeatherLayout.PillFontSize(s),
            WeatherLayout.PillPadX(s));

        _renderModel = model;
        return model;
    }
}

