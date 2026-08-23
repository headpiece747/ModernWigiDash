using System.Reflection;
using ModernWigiDash.Core.Models;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("weather_forecast", "Weather Forecast", Category = "Social & Visual", DefaultGridSize = GridSizePreset.Size5x4)]
public class WeatherForecastWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider, IWidgetLocationSearch, IWidgetEditorProvider, IWeatherFetchHost
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

        // One gated snapshot: the empty-check and the enumeration must see the
        // SAME candidates (an apply or an invalidation landing between two
        // reads would transiently disagree).
        var identity = _displayState.Identity;
        if (identity.Candidates.Count == 0) return [];

        // The empty "Automatic (by ranking)" entry lets a pick be cleared.
        return
        [
            new WidgetPropertyOption("", "Automatic (by ranking)"),
            .. identity.Candidates.Select(c => new WidgetPropertyOption(c.Query, c.Label))
        ];
    }

    private readonly WeatherClient _client;
    private readonly WeatherFetchFlow _flow;

    // The widget's gated display state is ONE module: the single gate, the
    // snapshot state, the resolved-identity twin (taken from the Fetched
    // outcome or the boot cache load — the client reports the identity once
    // per fetch, and the widget owns the dropdown, population, header title,
    // and the render-model cache key from it, so it never re-reads the
    // client's resolution state on the render path), the pending label
    // write-back, the last-success stamp, and the forecast render copies.
    // Every read and mutation runs under the module's one gate, so the seam
    // bodies and the render tick's lock region are forwards — the "one
    // consistent view" is a type, not a discipline repeated at every call
    // site. The UI-thread flush of the write-back stays here (it persists).
    private readonly WeatherDisplayState _displayState;

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

    public double? CurrentPopulation
    {
        get
        {
            // One gated snapshot: the predicate and the value come from the same
            // read (an invalidation between the two reads would return 0.0 where
            // null — "no data" — is the intent).
            var identity = _displayState.Identity;
            return identity.Population > 0 ? identity.Population : null;
        }
    }

    // -- IWidgetEditorProvider ------------------------------------------------

    public EditorKind? GetEditorKind(PropertyInfo property)
        => string.Equals(property.Name, nameof(Location), StringComparison.Ordinal) ? EditorKind.LocationSearch : null;

    // -- IWeatherFetchHost (the flow's host seam) ------------------------------
    // The flow owns the fetch sequence; these members carry the host
    // concerns across the seam. The gate discipline lives in the
    // display-state module — the apply, the version read, and the write-back
    // guard's check + set all run under its one gate — so these bodies are
    // forwards. Explicit implementations: the seam is for the flow, not for
    // callers.

    /// <summary>The flow's location read: the property → identity-input
    /// coercion (blank → null, trim of the optional fields).</summary>
    WeatherLocation IWeatherFetchHost.CurrentLocation => BuildLocation();

    /// <summary>The display state's data version (the module reads it under
    /// its gate — the apply writes it under the same gate).</summary>
    int IWeatherFetchHost.DataVersion => _displayState.DataVersion;

    /// <summary>The Static Snapshot property — the cadence gate's veto
    /// input.</summary>
    bool IWeatherFetchHost.IsStaticSnapshot => StaticSnapshot;

    /// <summary>The fetch cancellation token (teardown).</summary>
    CancellationToken IWeatherFetchHost.RunToken => _pollCts?.Token ?? CancellationToken.None;

    /// <summary>Queues a resolved-label write-back for the UI thread (the
    /// module applies the identity guard's check + set under its gate — one
    /// critical section).</summary>
    void IWeatherFetchHost.QueueLabelWriteback(Func<bool> identityGuard, string value)
        => _displayState.QueueLabelWriteback(identityGuard, value);

    /// <summary>Requests a canvas repaint.</summary>
    void IWeatherFetchHost.RequestRender() => Context?.RequestRender();

    /// <summary>Requests an inspector refresh (the Location Match candidates
    /// changed).</summary>
    void IWeatherFetchHost.RequestInspectorRefresh() => Context?.RequestInspectorRefresh();

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
        // The display-state module's clock seam is the CLIENT's clock, resolved
        // at stamp time (not captured at construction) — a test clock swap is
        // observed by the last-success stamp.
        _displayState = new(WeatherPresentation.UnknownLocationLabel, () => Clock.GetUtcNow().UtcDateTime);
        // The fetch flow owns the sequence; the host concerns travel across
        // the IWeatherFetchHost seam. This widget IS the production host
        // adapter: the display-state module carries the gate discipline
        // (the IWeatherFetchHost section below forwards to it), and the
        // flow's tests wrap the same module.
        _flow = new WeatherFetchFlow(_client, this);
    }

    /// <summary>Test seam: injectable clock for fetch throttling and cache timestamps (forwards to the client).</summary>
    internal TimeProvider Clock { get => _client.Clock; set => _client.Clock = value; }

    /// <summary>Test seam: substitute HTTP transport for fetch tests (forwards to the client).</summary>
    internal HttpClient? TestHttpClient { get => _client.TestHttpClient; set => _client.TestHttpClient = value; }

    /// <summary>The last resolved display name (test/UI seam: the widget's own
    /// copy of the identity the Fetched outcome reported).</summary>
    internal string ResolvedCityName => _displayState.Identity.ResolvedName;

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

    /// <summary>The snapshot display state (forwarded from the display-state
    /// module, whose gate owns the swap): the widget tests read the forecast
    /// lists and the version through it.</summary>
    internal WeatherSnapshotState _snapshotState => _displayState.State;

    // The per-mode draw paths live in the renderer (WeatherWidgetRenderer),
    // which owns the card/pill/row paints — one shared pair behind every
    // card, colors swapped via Paint.Color mutation (hoisted out of the
    // per-card loops). The header's two paints (title, unit badge) follow the
    // same hoisted pattern here — the 30 FPS render path allocates no paints.
    private readonly WeatherWidgetRenderer _renderer = new();
    private readonly SKPaint _titlePaint = new() { IsAntialias = true };
    private readonly SKPaint _unitPaint = new() { IsAntialias = true };
    private readonly SKPaint _subtitlePaint = new() { IsAntialias = true };
    private readonly SKPaint _stalePaint = new() { IsAntialias = true };

    private SKRect _lastBounds;
    private volatile bool _isFetching;

    // The staleness line changes at most once per second (the time-ago
    // buckets); the render recomputes it once per second, not once per frame.
    private DateTime _staleMemoLastSuccess;
    private bool _staleMemoFetching;
    private long _staleMemoElapsedSecond = -1;
    private string? _staleMemoText;

    private string? BuildStalenessLine(bool fetching, DateTime lastSuccess, DateTime now)
    {
        long elapsedSecond = lastSuccess <= DateTime.MinValue
            ? long.MinValue
            : (long)(now - lastSuccess).TotalSeconds;
        if (_staleMemoElapsedSecond == elapsedSecond && _staleMemoFetching == fetching && _staleMemoLastSuccess == lastSuccess)
        {
            return _staleMemoText;
        }

        _staleMemoElapsedSecond = elapsedSecond;
        _staleMemoFetching = fetching;
        _staleMemoLastSuccess = lastSuccess;
        _staleMemoText = WeatherPresentation.BuildStalenessText(fetching, lastSuccess, now);
        return _staleMemoText;
    }

    // The render-model cache: every formatted string the draw paths need is
    // rebuilt only when (data version, bounds, property snapshot) changes —
    // weather data moves at most every 15 minutes, so the static scene
    // allocates nothing on the 30 FPS render path.
    /// <summary>The cached render model (internal test seam: the invalidation
    /// matrix pins which keys force a rebuild).</summary>
    internal WeatherRenderModel? _renderModel;

    private static readonly string CacheDir = Path.Combine(AppContext.BaseDirectory, "weather_cache");
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
        _isFetching = true;
        _ = TrackFetchAsync(force);
    }

    private async Task TrackFetchAsync(bool force)
    {
        try
        {
            await _flow.RunFetchAsync(force).ConfigureAwait(false);
        }
        finally
        {
            _isFetching = false;
            Context?.RequestRender();
        }
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
        _subtitlePaint.Dispose();
        _stalePaint.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        // The drop granularity is the rule's decision (WeatherInvalidation):
        // a Location Match pick keeps the candidates it was offered from,
        // every other resolution input voids the whole identity (a stale pick
        // can never win). The two twins (the client's fetch control and the
        // widget's display state) take the SAME kind through their own gated
        // entry — the display-state transitions run under the SAME gate the
        // module's guarded apply takes, so the clear is atomic against an
        // in-flight fetch's assignment (either the assignment lands before
        // the clear and is erased, or the guard re-reads the new location and
        // the assignment never happens; the edit can never be resurrected
        // over). Both Invalidate entries also drop a PENDING resolved-label
        // write-back (the race a completed-but-unflushed fetch leaves) —
        // strictly stronger under the gate, safe because the identity
        // re-check is also under the gate.
        WeatherInvalidationKind kind = WeatherInvalidation.KindForProperty(propertyName);
        if (kind == WeatherInvalidationKind.Location && _suppressLocationWriteback)
        {
            // The resolved-label write-back skips the forced re-fetch: the
            // label was just resolved by the fetch that wrote it, so fetching
            // again would loop (the write-back converges after one extra
            // resolution at most).
            kind = WeatherInvalidationKind.None;
        }

        if (kind != WeatherInvalidationKind.None)
        {
            _displayState.Invalidate(kind);
            _client.Invalidate(kind);
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

        // Per-frame geometry: the scale and the header layout are computed
        // ONCE here and shared by the draw path and the render-model build —
        // one site reaches the layout module per frame.
        var (sx, sy, s) = WeatherLayout.Scale(bounds);
        var header = WeatherLayout.ComputeHeader(bounds, s, sy);
        // The badge draws the temperature unit; the build module derives the pair
        // from the key's UnitSystem (one rule, not two).
        var (tempUnit, _) = WeatherPresentation.ParseUnitSystem(UnitSystem);

        // One consistent view: the display-state module captures the state scalars,
        // the resolved identity, the last-success stamp (sharing its gate
        // with the apply's write — the stale-elapsed display value is one
        // consistent view, not a cross-thread struct read), and the
        // version-gated forecast copies, and hands the whole render-model
        // input back — the build module receives a torn-write-free view.
        var (buildInputs, lastSuccessFetchTime) = _displayState.CaptureRenderView(
            bounds, header, s,
            LayoutMode, UnitSystem, CustomLabel,
            ShowFeelsLike, ShowHumidity, ShowWind, ShowHighLow, ShowForecast,
            Location);

        // The render model owns every formatted string; the draw paths only
        // measure and paint. The build module is the ONE place the model is
        // composed — on a key hit it returns the cached model and the
        // per-frame path allocates nothing.
        var model = WeatherRenderModelFactory.Resolve(_renderModel, buildInputs);
        if (!ReferenceEquals(_renderModel, model)) _renderModel = model;

        var titleFont = WeatherWidgetRenderer.GetTitleFont(header.TitleFontSize);
        _titlePaint.Color = textPrimary;
        canvas.DrawTextWithFallback(model.TruncatedHeader, bounds.Left + header.Pad, header.HeaderTextY, titleFont, _titlePaint);

        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, WeatherLayout.BadgeFontSize(s));
        _unitPaint.Color = SKColors.White;
        float uW = FontHelper.MeasureTextWithFallback(tempUnit, unitFont);
        canvas.DrawTextWithFallback(tempUnit, header.BadgeRect.MidX - uW / 2f, header.BadgeRect.MidY + 4.5f * s, unitFont, _unitPaint);

        // Guidance or confirmation text, computed once per model rebuild (the
        // key includes CandidateCount).
        float subtitleH = 0f;
        if (model.SubtitleText is { Length: > 0 } subtitle)
        {
            float subtitleFontSize = Math.Clamp(header.TitleFontSize * 0.6f, 9f, 18f);
            var subtitleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, subtitleFontSize);
            _subtitlePaint.Color = new SKColor(255, 255, 255, 160);
            float subtitleY = header.HeaderTextY + header.TitleFontSize * 0.85f;
            canvas.DrawTextWithFallback(subtitle, bounds.Left + header.Pad, subtitleY, subtitleFont, _subtitlePaint);
            subtitleH = header.TitleFontSize * 1.1f;
        }

        // The staleness line's display rule (Updating… / time-ago / nothing) lives
        // in the presentation module; the string itself is memoized per second
        // (the time-ago buckets change at most once per second).
        string? staleText = BuildStalenessLine(
            _isFetching, lastSuccessFetchTime, Clock.GetUtcNow().UtcDateTime);
        float staleH = 0f;
        if (staleText is { Length: > 0 })
        {
            float staleFontSize = Math.Clamp(10f * s, 7f, 14f);
            var staleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, staleFontSize);
            _stalePaint.Color = new SKColor(255, 255, 255, 120);
            canvas.DrawTextWithFallback(staleText, bounds.Left + header.Pad, bounds.Bottom - header.Pad + 2f * s, staleFont, _stalePaint);
            staleH = staleFontSize * 1.4f;
        }

        SKRect contentBounds = new(
            bounds.Left + header.Pad,
            bounds.Top + header.HeaderHeight + 6f * sy + subtitleH,
            bounds.Right - header.Pad,
            bounds.Bottom - header.Pad - staleH);

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
    /// direct-drive tests). The take runs under the display-state gate (so a
    /// write-back queued concurrently can never be lost to it) and is cleared
    /// before the write so a re-entrant render cannot double-write; the
    /// suppression flag keeps the write's OnPropertyChanged from re-firing a
    /// fetch.
    /// </summary>
    internal void ApplyPendingLocationWriteback()
    {
        if (_displayState.TakePendingWriteback() is not { } pending) return;
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
    /// here and crosses the seam through <c>IWeatherFetchHost.CurrentLocation</c>.
    /// One read per fetch step — the flow never re-derives the shape.
    /// </summary>
    private WeatherLocation BuildLocation()
        => new(LocationType, Location, Latitude, Longitude, CustomLabel, string.IsNullOrWhiteSpace(CountryCode) ? null : CountryCode.Trim())
        {
            LocationMatch = string.IsNullOrWhiteSpace(LocationMatch) ? null : LocationMatch.Trim()
        };

    /// <summary>
    /// The flow's apply seam (a forward): applies a fetched/cached snapshot
    /// to the display state, keeping the "response omitted this section —
    /// keep the previous value" semantics. The guard (version-then-identity)
    /// and the merge (null-keeps + per-list version bump) live in
    /// <see cref="WeatherSnapshotApplyPolicy"/>; the gate discipline lives in
    /// the display-state module — under its one lock it asks the guard, swaps
    /// in the merge's new state, and applies the resolved-identity copies
    /// under the SAME lock, so an edit landing between the guard and the
    /// copies can no longer be resurrected over by the old identity's state
    /// (the edit's clear takes the same gate). The request's population field
    /// follows the client's no-data sentinel: 0 clears the resolved
    /// population (the fetch reported none), non-zero replaces — "no data"
    /// and "keep previous" are distinguishable by null vs. provided. Returns
    /// whether the snapshot was applied.
    /// </summary>
    bool IWeatherFetchHost.TryApply(WeatherApplyRequest request)
        => _displayState.TryApply(request);

    /// <summary>
    /// The host apply for a same-name tie (a forward): the display-state
    /// module runs the identity guard and the placeholder reset under its one
    /// gate (an edit that changed the resolution inputs since the fetch wins
    /// — the tie's candidates and header must not belong to the OLD
    /// identity), and this host supplies the queried-location read (the
    /// property coercion) that the module evaluates under the same lock.
    /// </summary>
    bool IWeatherFetchHost.TryApplyTie(IReadOnlyList<GeocodeCandidate> candidates, Func<bool> identityGuard)
        => _displayState.TryApplyTie(candidates, identityGuard, () => BuildLocation().Location);

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
}

