using System.Reflection;
using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("weather_forecast", "Weather Forecast", Category = "Social & Visual")]
public class WeatherForecastWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider, IWidgetLocationSearch, IWidgetEditorProvider
{
    public override SKSize DefaultSize => GridSizePreset.Size5x4.ToSize();

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
        if (_resolvedCandidates.Count == 0) return [];

        // The empty "Automatic (by ranking)" entry lets a pick be cleared.
        return
        [
            new WidgetPropertyOption("", "Automatic (by ranking)"),
            .. _resolvedCandidates.Select(c => new WidgetPropertyOption(c.Query, c.Label))
        ];
    }

    private readonly WeatherClient _client;

    // The widget's own copies of the resolved identity, taken from the
    // Fetched outcome (or the boot cache load): the client reports the
    // identity once per fetch, and the widget owns the dropdown, population,
    // header title, and the render-model cache key from these copies, so it
    // never re-reads the client's resolution state on the render path.
    private IReadOnlyList<GeocodeCandidate> _resolvedCandidates = [];
    private double _resolvedPopulation;
    // Neutral until a resolution sets a real identity (never a hardcoded city).
    private string _resolvedCityName = WeatherClient.UnknownLocationLabel;

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

    public double? CurrentPopulation => _resolvedPopulation > 0 ? _resolvedPopulation : null;

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
    }

    /// <summary>Test seam: injectable clock for fetch throttling and cache timestamps (forwards to the client).</summary>
    internal TimeProvider Clock { get => _client.Clock; set => _client.Clock = value; }

    /// <summary>Test seam: substitute HTTP transport for fetch tests (forwards to the client).</summary>
    internal HttpClient? TestHttpClient { get => _client.TestHttpClient; set => _client.TestHttpClient = value; }

    /// <summary>The last resolved display name (test/UI seam: the widget's own
    /// copy of the identity the Fetched outcome reported).</summary>
    internal string ResolvedCityName => _resolvedCityName;

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
        => ProfileOps.IsSafeInstanceId(instanceId) ? instanceId! : _safeCacheFallbackToken;

    /// <summary>Completed-fetch count (test seam: wait on fetch completion, not call start).</summary>
    internal int FetchCompletedCount => _client.FetchCompletedCount;

    private double _currentTempC = 25.0; // 77°F default
    private double _feelsLikeC = 22.2;  // 72°F default
    private double _humidity = 87.0;
    private double _windSpeedKmH = 16.1; // 10 mph default
    private int _weatherCode = 51;      // Drizzle default
    private double _highTempC = 26.6;   // 80°F default
    private double _lowTempC = 20.5;    // 69°F default

    // The per-mode draw paths live in the renderer (WeatherWidgetRenderer),
    // which owns the card/pill/row paints — one shared pair behind every
    // card, colors swapped via Paint.Color mutation (hoisted out of the
    // per-card loops). The header's two paints (title, unit badge) follow the
    // same hoisted pattern here — the 30 FPS render path allocates no paints.
    private readonly WeatherWidgetRenderer _renderer = new();
    private readonly SKPaint _titlePaint = new() { IsAntialias = true };
    private readonly SKPaint _unitPaint = new() { IsAntialias = true };

    internal readonly List<DailyForecastItem> _dailyForecasts = [];
    internal readonly List<HourlyForecastItem> _hourlyForecasts = [];
    private readonly Lock _forecastGate = new();
    private IReadOnlyList<DailyForecastItem> _dailyForecastSnapshot = [];
    private IReadOnlyList<HourlyForecastItem> _hourlyForecastSnapshot = [];
    private int _forecastVersion;
    private int _renderedForecastVersion = -1;
    private SKRect _lastBounds;

    // The render-model cache: every formatted string the draw paths need is
    // rebuilt only when (data version, bounds, property snapshot) changes —
    // weather data moves at most every 15 minutes, so the static scene
    // allocates nothing on the 30 FPS render path.
    private int _dataVersion;
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
            "WEATHER", WeatherClient.FetchWindow, () => true,
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
    /// The single "fetch if due" gate for every cadence source: the refresh
    /// PollLoop, the per-frame render kick, and the property-change force
    /// paths all call this. The static-snapshot rule and the client's
    /// throttle-window pre-check are applied here, once, instead of being
    /// re-derived at each call site; the client's atomic in-flight claim
    /// remains the authority (see <see cref="WeatherClient.FetchCurrentAsync"/>).
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

    /// <summary>
    /// The resolution inputs that force a re-fetch on change — the widget-side
    /// mirror of <see cref="WeatherClient.BuildQueryKey"/> (every key field
    /// except LocationMatch, which has its own branch in OnPropertyChanged).
    /// The drift test pins this set to the WeatherLocation record, so a new
    /// resolution input can never change the identity without a re-fetch.
    /// </summary>
    internal static readonly string[] ResolutionInvalidationProperties =
        [nameof(Location), nameof(Latitude), nameof(Longitude), nameof(CountryCode), nameof(LocationType)];

    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        // A Location Match pick resolves against the candidates it was offered
        // from, so it keeps them (InvalidateCoordinates); every other location
        // change clears the candidates so a stale pick can never win.
        // Any resolution-input edit also drops a PENDING resolved-label
        // write-back: a fetch that completed just before the edit may have set
        // the pending label, and the next Render tick would otherwise write it
        // over the newer edit (the client's in-flight stale guard only covers
        // fetches still in flight at edit time — a completed fetch whose
        // write-back was not yet flushed is the race the clear closes).
        if (string.Equals(propertyName, nameof(LocationMatch), StringComparison.Ordinal))
        {
            _pendingLocationWriteback = null;
            // Mirror the client's InvalidateCoordinates: the resolved name and
            // population drop with the old resolution, but the candidates stay
            // (the pick resolves against the candidates it was offered from).
            _resolvedCityName = "";
            _resolvedPopulation = 0;
            _client.InvalidateCoordinates();
            RequestRefresh(force: true);
        }
        else if (!_suppressLocationWriteback && ResolutionInvalidationProperties.Contains(propertyName))
        {
            // The resolved-label write-back skips the forced re-fetch: the
            // label was just resolved by the fetch that wrote it, so fetching
            // again would loop (the write-back converges after one extra
            // resolution at most).
            _pendingLocationWriteback = null;
            // Mirror the client's InvalidateLocation: the whole resolved
            // identity (candidates, population, name) is void until the next
            // fetch resolves the new input, so the render-model cache key
            // turns and the header drops the old city immediately.
            _resolvedCandidates = [];
            _resolvedCityName = "";
            _resolvedPopulation = 0;
            _client.InvalidateLocation();
            RequestRefresh(force: true);
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

    /// <summary>
    /// Whether the fetch cadence gate blocks a non-forced fetch: while a
    /// static snapshot is showing (after a boot load), the render tick's
    /// request is skipped until the snapshot is dismissed.
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

    /// <summary>Suppresses the OnPropertyChanged fetch while the resolved label
    /// is written back after a successful resolution (the write-back must not
    /// re-fire a fetch).</summary>
    internal bool _suppressLocationWriteback;

    /// <summary>
    /// The resolved label awaiting its UI-thread write-back: the fetch
    /// continuation (thread pool) only sets this; <see cref="Render"/> (UI
    /// thread, 30 FPS) performs the actual SetProperty via
    /// <see cref="ApplyPendingLocationWriteback"/>, so Context.PersistProperty
    /// stays on the UI thread and a stale fetch can never clobber a newer edit.
    /// </summary>
    private string? _pendingLocationWriteback;

    internal async Task FetchLiveWeatherAsync(bool force = false)
    {
        // The query key at START: the client's Stale verdict is computed
        // before its own cache-save await, so an identity change landing
        // after that verdict but before this continuation runs would come
        // back as Fetched — re-validate at the end of the await.
        string fetchKey = WeatherClient.BuildQueryKey(BuildLocation());
        WeatherFetchResult result;
        try
        {
            result = await _client.FetchCurrentAsync(BuildLocation(), force, _pollCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Teardown: the widget's poll CTS was cancelled (dispose) — a
            // cancelled fetch is not a failure, so nothing is logged or applied.
            return;
        }

        if (result is WeatherFetchResult.Stale)
        {
            // The resolution identity changed while the fetch was in flight
            // (the widget's invalidation cleared the client's query identity):
            // the client dropped the stale result — weather AND label — without
            // stamping the throttle. Re-fetch the new identity immediately,
            // since the edit-time force refresh was swallowed by the in-flight
            // claim, which this fetch's completion has now released.
            RequestRefresh(force: true);
            return;
        }

        // Post-await re-validation: the client's Stale verdict is computed
        // before its cache-save await; a resolution-input change (including
        // the post-InitializeAsync profile hydration) landing in that window
        // returns Fetched. Drop the result — weather AND label — when the
        // identity no longer matches, exactly as the old in-widget guard did.
        if (!string.Equals(fetchKey, WeatherClient.BuildQueryKey(BuildLocation()), StringComparison.Ordinal))
        {
            RequestRefresh(force: true);
            return;
        }

        if (result is not WeatherFetchResult.Fetched fetched)
        {
            // Throttled / InFlight / Failed: keep the previous state silently.
            return;
        }

        // The apply is identity-guarded under the same lock as the version
        // checks: an edit landing between the post-await re-check above and
        // this point must win — the snapshot and the resolved-identity copies
        // must not belong to the OLD identity (the stale write-back is
        // protected separately below). The resolved-identity copies are
        // assigned under the same lock — an edit cannot resurrect the old
        // dropdown/name/population over the fresh edit.
        if (!ApplySnapshot(fetched.Snapshot,
                identityGuard: () => string.Equals(fetchKey, WeatherClient.BuildQueryKey(BuildLocation()), StringComparison.Ordinal),
                candidates: fetched.Candidates,
                population: fetched.Population,
                resolvedName: fetched.Snapshot.ResolvedCityName))
        {
            RequestRefresh(force: true);
            return;
        }

        WeatherSnapshot snapshot = fetched.Snapshot;

        // The resolved label's write-back is deferred to the UI thread (Render
        // flushes the pending field): Context.PersistProperty stays on the UI
        // thread, and the identity guard above already dropped any fetch whose
        // resolution inputs changed while it was in flight. The write-back is
        // skipped entirely when a CustomLabel supplies the title: the label is
        // display-only, and writing it into Location would destroy the query
        // (explicit-coords/pick + CustomLabel would overwrite "New York" with
        // "Home" in the profile). The identity is re-validated once more at
        // the set: an edit landing between the re-check above and this
        // assignment must win (the pending set would otherwise flush the OLD
        // identity's label over the fresh edit on the next render).
        if (!string.IsNullOrWhiteSpace(snapshot.ResolvedCityName)
            && string.IsNullOrWhiteSpace(CustomLabel)
            && !string.Equals(snapshot.ResolvedCityName, Location, StringComparison.Ordinal)
            && string.Equals(fetchKey, WeatherClient.BuildQueryKey(BuildLocation()), StringComparison.Ordinal))
        {
            _pendingLocationWriteback = snapshot.ResolvedCityName;
        }

        // The geocode may have produced new Location Match candidates: refresh
        // the inspector so an already-open panel shows the dropdown (the Twitch
        // pattern — the renderer only builds a ComboBox when options exist).
        // Only when the option set changed — see _lastInspectorCandidatesStamp.
        string stamp = string.Join('\n', _resolvedCandidates.Select(c => c.Query));
        if (!string.Equals(stamp, _lastInspectorCandidatesStamp, StringComparison.Ordinal))
        {
            _lastInspectorCandidatesStamp = stamp;
            Context?.RequestInspectorRefresh();
        }

        Context?.RequestRender();
    }

    /// <summary>
    /// Performs the deferred resolved-label write-back on the UI thread (called
    /// by <see cref="Render"/> at 30 FPS; also an internal test seam for
    /// direct-drive tests). The pending field is cleared before the write so a
    /// re-entrant render cannot double-write; the suppression flag keeps the
    /// write's OnPropertyChanged from re-firing a fetch.
    /// </summary>
    internal void ApplyPendingLocationWriteback()
    {
        if (_pendingLocationWriteback is not { } pending) return;
        _pendingLocationWriteback = null;
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

    private WeatherLocation BuildLocation()
        => new(LocationType, Location, Latitude, Longitude, CustomLabel, string.IsNullOrWhiteSpace(CountryCode) ? null : CountryCode.Trim())
        {
            LocationMatch = string.IsNullOrWhiteSpace(LocationMatch) ? null : LocationMatch.Trim()
        };

    /// <summary>
    /// Applies a fetched/cached snapshot to the render fields, keeping the
    /// "response omitted this section — keep the previous value" semantics.
    /// When <paramref name="expectedVersion"/> is set, the apply is skipped —
    /// atomically, under the same lock that bumps <see cref="_dataVersion"/>
    /// — if the data version moved on (a fetch landed while a cache load was
    /// in flight must never be overwritten by the stale cache).
    /// <paramref name="identityGuard"/> is evaluated under the same lock:
    /// the cache may also be skipped when the resolution identity changed
    /// (the boot load runs pre-hydration). The resolved-identity copies
    /// (dropdown, population, header name) are assigned under the SAME lock
    /// when provided — an edit landing between the guard and the copies can
    /// no longer be resurrected over by the old identity's state (the edit
    /// cleared the copies; the guarded assignment re-populates them only
    /// when the identity still matches). Returns whether the snapshot was
    /// applied.
    /// </summary>
    private bool ApplySnapshot(WeatherSnapshot snapshot, int? expectedVersion = null, Func<bool>? identityGuard = null,
        IReadOnlyList<GeocodeCandidate>? candidates = null, double population = 0, string? resolvedName = null)
    {
        lock (_forecastGate)
        {
            if (expectedVersion is int expected && _dataVersion != expected) return false;
            if (identityGuard is not null && !identityGuard()) return false;
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
            if (candidates is not null) _resolvedCandidates = candidates;
            if (population != 0) _resolvedPopulation = population;
            if (resolvedName is not null) _resolvedCityName = resolvedName;
            return true;
        }
    }

    /// <summary>Test seam: replaces the client cache-load leg so the boot-race
    /// version guard is drivable deterministically (defaults to the client's
    /// identity-checked load).</summary>
    internal Func<WeatherLocation, CancellationToken, Task<WeatherSnapshot?>>? CacheLoadOverride { get; set; }

    /// <summary>
    /// The boot cache load. The data version is captured BEFORE the await and
    /// re-checked after, so a fetch that landed while the load was in flight
    /// (InitializeAsync fires the load and the boot fetch concurrently) can
    /// never be overwritten by the stale cache.
    /// </summary>
    internal async Task LoadCachedWeatherAsync(CancellationToken cancellationToken)
    {
        try
        {
            int versionBefore;
            string locationKeyBefore;
            lock (_forecastGate) { versionBefore = _dataVersion; }
            locationKeyBefore = WeatherClient.BuildQueryKey(BuildLocation());

            // The cache is identity-checked against the CURRENT location: a
            // cache saved for a different resolution must not surface as fresh
            // weather (the client rejects a stamp mismatch).
            var load = CacheLoadOverride ?? _client.LoadCacheAsync;
            var cached = await load(BuildLocation(), cancellationToken).ConfigureAwait(false);
            if (cached is null) return;

            // The version + identity guards run INSIDE ApplySnapshot's lock:
            // a fetch that landed during the await (version) or a hydration
            // that changed the location (identity) must both win over the
            // stale cache, and the check + apply are one atomic step. The
            // boot load runs pre-hydration with the DEFAULT location, so the
            // identity guard is what keeps the default-stamped cache from
            // surfacing under the profile's real location. The widget's
            // resolved-name copy restores under the same lock (the cache
            // cannot carry candidates or population; they stay empty,
            // exactly like the client's own load state).
            bool applied = ApplySnapshot(cached, expectedVersion: versionBefore,
                identityGuard: () => string.Equals(locationKeyBefore, WeatherClient.BuildQueryKey(BuildLocation()), StringComparison.Ordinal),
                resolvedName: cached.ResolvedCityName);
            if (!applied)
            {
                // The client's load already mutated its resolution state
                // (name/lat/lon/throttle) — roll it back when the identity
                // changed so the next resolution starts clean (a version-only
                // skip means a fresh fetch already landed; nothing to undo).
                bool identityChanged;
                lock (_forecastGate)
                {
                    identityChanged = !string.Equals(locationKeyBefore, WeatherClient.BuildQueryKey(BuildLocation()), StringComparison.Ordinal);
                }
                if (identityChanged)
                {
                    _client.InvalidateCoordinates();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Teardown: the poll CTS was cancelled (dispose) — a cancelled
            // cache load is not a failure, so nothing is logged or applied.
        }
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
        int weatherCode, dataVersion;
        IReadOnlyList<DailyForecastItem> daily;
        IReadOnlyList<HourlyForecastItem> hourly;
        lock (_forecastGate)
        {
            dataVersion = _dataVersion;
            currentTempC = _currentTempC;
            feelsLikeC = _feelsLikeC;
            humidity = _humidity;
            windSpeedKmH = _windSpeedKmH;
            highTempC = _highTempC;
            lowTempC = _lowTempC;
            weatherCode = _weatherCode;
            daily = _dailyForecastSnapshot;
            hourly = _hourlyForecastSnapshot;
        }

        if (_renderModel is { } cached && IsCacheValid(cached, dataVersion, bounds))
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
            DataVersion = dataVersion,
            Bounds = bounds,
            LayoutMode = LayoutMode,
            UnitSystem = UnitSystem,
            CustomLabel = CustomLabel,
            ResolvedCity = _resolvedCityName,
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
        string cityRaw = string.IsNullOrWhiteSpace(CustomLabel) ? _resolvedCityName : CustomLabel;
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

    /// <summary>The render-model cache key: the data version, the bounds
    /// (layout-derived font sizes), and the property snapshot that changes any
    /// formatted string.</summary>
    private bool IsCacheValid(WeatherRenderModel cached, int dataVersion, SKRect bounds)
        => cached.DataVersion == dataVersion
            && cached.Bounds == bounds
            && string.Equals(cached.LayoutMode, LayoutMode, StringComparison.Ordinal)
            && string.Equals(cached.UnitSystem, UnitSystem, StringComparison.Ordinal)
            && string.Equals(cached.CustomLabel, CustomLabel, StringComparison.Ordinal)
            && string.Equals(cached.ResolvedCity, _resolvedCityName, StringComparison.Ordinal)
            && cached.ShowFeelsLike == ShowFeelsLike
            && cached.ShowHumidity == ShowHumidity
            && cached.ShowWind == ShowWind
            && cached.ShowHighLow == ShowHighLow
            && cached.ShowForecast == ShowForecast;
}

