using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Deep weather data module: resolve (geocode) → fetch → parse → disk cache,
/// with an internal 5-minute fetch throttle. The geocoding HTTP + parse half
/// lives in <see cref="WeatherGeocoder"/>; this class owns the fetch claim,
/// cache, forecast parsing, and the resolved-state routing (lat/lon/city
/// fields). The widget layer only renders snapshots returned by
/// <see cref="FetchCurrentAsync"/>.
/// </summary>
internal sealed class WeatherClient
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        EnableMultipleHttp2Connections = true
    })
    {
        // A hung upstream must not stall the fetch claim (and the widget's
        // render-kick cadence) indefinitely — 30s is the bound for every leg
        // that rides the shared client.
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>The fetch cool-down window — the one cadence constant the
    /// widget's refresh loop and every throttle check share (a change edits
    /// one value).</summary>
    internal static readonly TimeSpan FetchWindow = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private DateTime _lastFetchTime = DateTime.MinValue;
    private int _claim; // 1 = a fetch is in flight
    private string _lastLocationQuery = "";
    private double? _lat;
    private double? _lon;
    private WeatherResolutionState _identity;
    private string? _pendingWriteback;
    private readonly string _neutralLocationLabel;

    private readonly WeatherGeocoder _geocoder;

    private readonly WeatherCacheStore _cache;
    private readonly Action<string, Exception?>? _logError;

    /// <summary>Test seam: injectable clock for fetch throttling (read at stamp
    /// time, so a swap is observed by the next transition).</summary>
    internal TimeProvider Clock { get; set; }

    private HttpClient? _testHttpClient;

    /// <summary>Test seam: substitute HTTP transport for fetch tests (defaults to <see cref="SharedHttpClient"/>).
    /// The geocoder is constructed with THIS seam's live provider, so both
    /// fetch legs are drivable from one property — no sync step to keep in
    /// agreement.</summary>
    internal HttpClient? TestHttpClient
    {
        get => _testHttpClient;
        set => _testHttpClient = value;
    }

    private HttpClient Http => TestHttpClient ?? SharedHttpClient;

    /// <summary>The geocoding adapter (test seam: instance-scoped seams like
    /// <see cref="WeatherGeocoder.HttpTimeoutOverride"/> are reachable through
    /// this, so the timeout path is drivable per client, never process-wide).</summary>
    internal WeatherGeocoder Geocoder => _geocoder;

    /// <summary>
    /// Number of completed fetches (success or failure) — a test seam for
    /// waiting on fetch completion, since the in-flight claim releases only
    /// when <see cref="FetchCurrentAsync"/> returns.
    /// </summary>
    internal int FetchCompletedCount { get; private set; }

    /// <summary>Whether the throttle has ever been stamped — by a fetch
    /// attempt (failures cool down like successes) or a cache load. The cadence
    /// gate reads this as a named client fact, never by comparing the raw
    /// timestamp against a sentinel.</summary>
    internal bool HasFetched => _lastFetchTime != DateTime.MinValue;

    /// <param name="cacheDirectory">Directory for the disk cache (created on demand).</param>
    /// <param name="cacheFileName">Per-instance cache file name; defaults to a shared "weather_default.json".</param>
    /// <param name="timeProvider">Test seam: clock for throttling and cache timestamps.</param>
    /// <param name="http">Test seam: substitute HTTP transport (defaults to the shared client).</param>
    /// <param name="logError">Optional error sink; when omitted, failures are silent.</param>
    public WeatherClient(string cacheDirectory, string? cacheFileName = null, TimeProvider? timeProvider = null, HttpClient? http = null, Action<string, Exception?>? logError = null)
        : this(cacheDirectory, () => cacheFileName ?? "weather_default.json", timeProvider, http, logError, WeatherPresentation.UnknownLocationLabel)
    {
    }

    /// <summary>
    /// Test/Internal seam: resolves the cache file name lazily, at each
    /// load/save, from a provider. A widget whose <c>InstanceId</c> is assigned
    /// after construction (RehydrateWidget sets it before InitializeAsync)
    /// must key its cache by that final identity — baking the name at
    /// construction would orphan every write under a never-reused GUID.
    /// </summary>
    internal WeatherClient(string cacheDirectory, Func<string> cacheFileNameProvider, TimeProvider? timeProvider = null, HttpClient? http = null, Action<string, Exception?>? logError = null, string? neutralLocationLabel = null)
    {
        _cache = new WeatherCacheStore(cacheDirectory, cacheFileNameProvider, logError);
        _neutralLocationLabel = neutralLocationLabel ?? WeatherPresentation.UnknownLocationLabel;
        _identity = new(_neutralLocationLabel, 0, []);
        Clock = timeProvider ?? TimeProvider.System;
        _logError = logError;
        _geocoder = new WeatherGeocoder(() => Http, _logError);
        TestHttpClient = http;
        Directory.CreateDirectory(cacheDirectory);
    }

    /// <summary>The cache file name the store's provider currently resolves (test seam).</summary>
    internal string CacheFileName => _cache.CacheFileName;

    /// <summary>
    /// Test seam: an await that runs inside <see cref="FetchCurrentAsync"/>'s
    /// capture window, after the cache save completes and before the post-save
    /// re-validation. A test parks a fetch on it and lands an invalidation
    /// there, driving the stale-during-save-window race deterministically.
    /// </summary>
    internal Func<CancellationToken, Task>? SaveAwaitSeam { get; set; }

    /// <summary>
    /// Sync throttle pre-check for the render tick: true when the throttle
    /// window has elapsed since the last attempt. The first attempt
    /// (never-fetched) reads as elapsed; a failed attempt stamps the time,
    /// so failures cool down like successes. The window is the single
    /// <see cref="FetchWindow"/> both this check and the atomic claim share —
    /// one spelling, drift impossible.
    /// </summary>
    internal bool IsFetchWindowElapsed()
        => Clock.GetUtcNow().UtcDateTime - _lastFetchTime >= FetchWindow;

    /// <summary>
    /// Whether a fetch is in flight (the single-flight claim is held). The ONE
    /// in-flight fact the display's staleness line reads: the claim brackets the
    /// whole fetch — including the flow's fire-and-forget nested re-fetches and
    /// the boot fetch, which never set a widget-local flag — so the "Updating…"
    /// indicator can no longer drift from the fetch the client actually owns.
    /// Read without the gate, the same tolerance as <see cref="IsFetchWindowElapsed"/>:
    /// a flag read is a benign boolean for the display.
    /// </summary>
    internal bool IsFetchInFlight => _claim != 0;

    /// <summary>
    /// The single edit-path invalidation, per drop kind: resets the resolved
    /// coordinates and the throttle so the next fetch re-resolves and runs
    /// immediately, and drops the shared identity through the single rule —
    /// the Location Match pick (Coordinates kind) keeps the geocode candidates
    /// the pick resolves against; every other resolution input (Location kind)
    /// voids the whole identity, candidates included, so a stale pick can
    /// never win. The widget's edit path rides this one entry.
    /// </summary>
    internal void Invalidate(WeatherInvalidationKind kind)
    {
        lock (_gate)
        {
            _lat = null;
            _lon = null;
            _pendingWriteback = null;
            _identity = WeatherInvalidation.Drop(kind, _identity);
            _lastFetchTime = DateTime.MinValue;
            _lastLocationQuery = "";
        }
    }

    /// <summary>
    /// The boot-load's discarded-load rollback: undoes the client-side
    /// commitment a cache load made (the throttle stamp and the identity
    /// query) AND restores the shared resolved-identity value to what it was
    /// BEFORE the load — the widget's header name survives a rejected load,
    /// while the next fetch still re-resolves and runs immediately. Distinct
    /// from <see cref="Invalidate"/>: that entry serves an EDIT (a new place
    /// was chosen, so the old identity's name must go); here the identity
    /// never changed, only the load's own commitment is withdrawn. The
    /// coordinates are restored from the committed payload (the load's applied
    /// lat/lon).
    /// </summary>
    internal void RollbackCacheLoad(double? lat, double? lon, WeatherResolutionState preLoadIdentity)
    {
        lock (_gate)
        {
            _lat = lat;
            _lon = lon;
            _identity = preLoadIdentity;
            _lastFetchTime = DateTime.MinValue;
            _lastLocationQuery = "";
        }
    }

    /// <summary>
    /// The shared resolved-identity value — the ONE storage both the
    /// client and the display state read; its transitions run only through
    /// this module's gated members.
    /// </summary>
    internal WeatherResolutionState Identity
    {
        get { lock (_gate) { return _identity; } }
    }

    /// <summary>The pending resolved-label write-back awaiting the
    /// UI-thread flush — read under the gate (the queue and the take run
    /// under it, so a read in between is consistent).</summary>
    internal string? PendingLabelWriteback
    {
        get { lock (_gate) { return _pendingWriteback; } }
    }

    /// <summary>
    /// The one spelling of "the resolved label may still be written into
    /// Location": the name is non-empty, no CustomLabel claims the title (a
    /// label is display-only — writing the resolved name into Location would
    /// destroy the query), and the name is not already the Location (a
    /// no-op write would only churn a persistence + property event). The
    /// flow's queue and this take evaluate the SAME policy, and the take
    /// evaluates it under the gate — so an edit (a CustomLabel or a Location
    /// change) landing between the queue and the flush takes the same gate
    /// and is seen at the take, never sailed through an ungated flush check.
    /// </summary>
    internal static bool WritebackEligible(string? name, WeatherLocation currentLocation)
        => !string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(currentLocation.CustomLabel)
            && !string.Equals(name, currentLocation.Location, StringComparison.Ordinal);

    /// <summary>
    /// Queues a resolved-label write-back for the UI thread, only when the
    /// identity guard still passes — the check + set under the gate is one
    /// critical section (the edit-side clears and the UI-thread take take the
    /// same gate, so an edit either erases the queued value or is seen by the
    /// guard, and the take can never drop a concurrent queue). The queue
    /// carries only the name — the write-back eligibility decision is
    /// <see cref="WritebackEligible"/>, re-evaluated under the gate at take.
    /// </summary>
    internal void QueueLabelWriteback(Func<bool> identityGuard, string value)
    {
        lock (_gate)
        {
            if (identityGuard())
            {
                _pendingWriteback = value;
            }
        }
    }

    /// <summary>
    /// Returns and clears the pending write-back (the UI-thread flush) —
    /// under the gate, so a queue landing between the read and the clear can
    /// never be lost: the queue and the take serialize on the same lock. The
    /// take also decides whether the write may happen at all
    /// (<see cref="WritebackEligible"/> + the host's suppression flag): a
    /// vetoed take refuses AND KEEPS the value queued (a veto is a "not
    /// yet", never a "never" — a no-op write or a CustomLabel set between
    /// the queue and the flush must not silently lose the resolved label),
    /// so the next frame re-decides against the current host facts.
    /// </summary>
    internal string? TakePendingWriteback(WeatherLocation currentLocation, Func<bool> suppressed)
    {
        lock (_gate)
        {
            if (suppressed()) return null;
            if (!WritebackEligible(_pendingWriteback, currentLocation)) return null;
            string? pending = _pendingWriteback;
            _pendingWriteback = null;
            return pending;
        }
    }

    /// <summary>
    /// The display-state apply seam's identity half: the null-keeps
    /// replacement (the "response omitted this section — keep the previous
    /// value" rule shared with the snapshot merge; a provided population of
    /// 0 is the client's no-data sentinel: it clears, it does not keep) runs
    /// under the gate, so the identity copies land atomically with whatever
    /// the caller commits alongside them.
    /// </summary>
    internal void ApplyIdentity(string? resolvedName, double? population, IReadOnlyList<GeocodeCandidate>? candidates)
    {
        lock (_gate)
        {
            _identity = _identity.With(resolvedName, population, candidates);
        }
    }

    /// <summary>
    /// The display-state tie seam's identity half: the tied candidates become
    /// the dropdown, the queried name becomes the honest header (there is no
    /// winner to name; a blank query takes the neutral label), and the
    /// population clears. Runs under the gate so the reset is atomic with
    /// the state reset the caller commits alongside it.
    /// </summary>
    internal void ApplyTieIdentity(string? queriedLocation, IReadOnlyList<GeocodeCandidate> candidates)
    {
        lock (_gate)
        {
            _identity = _identity.With(string.IsNullOrWhiteSpace(queriedLocation) ? _neutralLocationLabel : queriedLocation, 0, candidates);
        }
    }

    /// <summary>
    /// Resolves the location (geocode or explicit coordinates), fetches current
    /// + hourly + daily weather from Open-Meteo in one request, parses it into a
    /// snapshot, and writes the disk cache. The outcome reports WHY no snapshot
    /// came back when the caller cannot apply one — the widget distinguishes
    /// "try again now" (Stale) from "keep what you have" (Throttled, InFlight,
    /// Failed).
    /// </summary>
    public async Task<WeatherFetchResult> FetchCurrentAsync(WeatherLocation location, bool force = false, CancellationToken cancellationToken = default)
    {
        // The resolution identity this fetch started for — captured once so
        // the completion check (and the cache stamp) cannot drift from the
        // location that actually resolved.
        string fetchQueryKey = WeatherQueryKey.Build(location);

        // The capture window (ADR-0006), named: this fetch's start key
        // against the resolution's live identity state. The re-check sites
        // below (the re-resolve condition, the no-coordinates stale check,
        // the post-save re-validation) all route through the guard — one
        // rule; the atomic stamp transitions (ConfirmAndStamp, Stamp) keep
        // their gate atomicity.
        var window = new CaptureWindowGuard(fetchQueryKey, () => _lastLocationQuery);

        // The claim + throttle rules live in the client: the
        // in-flight guard is Interlocked — the render tick, the refresh
        // timer, and OnTouch can race, and a check-then-set would let two of
        // them through. The claim's failure reason is reported so the caller
        // can tell "already being fetched" from "cooling down".
        var begin = Begin(force);
        if (begin == BeginResult.InFlight) return new WeatherFetchResult.InFlight();
        if (begin == BeginResult.Throttled) return new WeatherFetchResult.Throttled();

        try
        {
            WeatherResolutionOutcome? resolution = null;
            if (!_lat.HasValue || window.Dropped || force)
                resolution = await ResolveCoordinatesAsync(location, fetchQueryKey, cancellationToken).ConfigureAwait(false);

            if (!_lat.HasValue || !_lon.HasValue)
            {
                return BuildNoCoordinatesVerdict(window, resolution, fetchQueryKey);
            }

            double lat = _lat.Value;
            double lon = _lon.Value;

            var snapshot = await BuildSnapshotAsync(lat, lon, _identity.ResolvedName, cancellationToken)
                .ConfigureAwait(false);

            // The stale check: the widget invalidates the client (clearing
            // the identity query) when ANY resolution input changes. If that
            // happened while this fetch was in flight, the resolved identity
            // no longer matches the one this fetch started for — the snapshot
            // is stale: no throttle stamp (the new identity's fetch must not
            // cool down) and no cache write. ConfirmAndStamp compares, stamps,
            // and captures the resolved-identity payload under ONE gate, so a
            // concurrent invalidation cannot tear the comparison.
            if (!ConfirmAndStamp(fetchQueryKey, out var candidates, out var population))
            {
                return new WeatherFetchResult.Stale(fetchQueryKey);
            }
            var fetched = new WeatherFetchResult.Fetched(snapshot, candidates, population, fetchQueryKey);

            await _cache.SaveAsync(snapshot, fetchQueryKey, cancellationToken).ConfigureAwait(false);
            // The cache write is part of the capture window: an invalidation
            // landing while the save is in flight makes the snapshot stale AT
            // COMPLETION — the Stale verdict must cover the whole window, not
            // stop at the identity confirmation above. The file that was
            // written stays on disk stamped with the OLD identity: the cache
            // load's stamp check rejects it, and the invalidation that caused
            // the staleness already reset the throttle, so the new identity
            // re-fetches immediately.
            if (SaveAwaitSeam is { } saveSeam)
            {
                await saveSeam(cancellationToken).ConfigureAwait(false);
            }
            if (window.Dropped)
            {
                return new WeatherFetchResult.Stale(fetchQueryKey);
            }
            return fetched;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"Weather fetch failed: {ex.Message}", ex);
            // Stamp the attempt time so a failure cools down like a success —
            // otherwise the widget's render tick sees an elapsed window and
            // retries at frame rate during an outage (request + log storm).
            // EXCEPT when the identity changed mid-flight: a stale failure is
            // like a stale success — it must not block the re-fetch of the
            // new identity, and the status must SAY so.
            return Stamp(fetchQueryKey)
                ? new WeatherFetchResult.Failed()
                : new WeatherFetchResult.Stale(fetchQueryKey);
        }
        finally
        {
            End();
            FetchCompletedCount++;
        }
    }

    /// <summary>
    /// The no-coordinates verdict: the resolution failed, was left
    /// unresolved, or refused to break a tie. If the identity changed while
    /// the fetch was in flight, this is a STALE failure (the stale success
    /// path's verdict): the widget must re-fetch the new identity
    /// immediately, not treat it as a plain failed attempt. A genuine tie
    /// CARRIES the tied candidates (they are the widget's Location Match
    /// dropdown) instead of collapsing into a bare failure: the user should
    /// be offered the pick, not a dead end. Every other no-coordinates
    /// outcome (a failed geocode, or an empty-candidate tie that is
    /// unresolvable anyway) stays a plain failure.
    /// </summary>
    private static WeatherFetchResult BuildNoCoordinatesVerdict(CaptureWindowGuard window, WeatherResolutionOutcome? resolution, string fetchQueryKey)
    {
        if (window.Dropped)
        {
            return new WeatherFetchResult.Stale(fetchQueryKey);
        }
        if (resolution is WeatherResolutionOutcome.Ambiguous ambiguous && ambiguous.Candidates.Count > 0)
        {
            return new WeatherFetchResult.Tie(ambiguous.Candidates, fetchQueryKey);
        }
        return new WeatherFetchResult.Failed();
    }

    /// <summary>
    /// The forecast leg: reads the Open-Meteo current + hourly + daily
    /// response for the resolved coordinates, parses each block through the
    /// pure parser rules, and assembles the snapshot. The URL's invariant
    /// F4 formatting lives in the resolver behind the geocoder's door (a
    /// comma-decimal OS locale must never interpolate "40,7100" into the
    /// query at a call site).
    /// </summary>
    private async Task<WeatherSnapshot> BuildSnapshotAsync(double lat, double lon, string cityName, CancellationToken cancellationToken)
    {
        string json = await _geocoder.ReadForecastAsync(lat, lon, cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var (tempC, feelsLikeC, windSpeedKmH, weatherCode, isDay) = WeatherForecastParser.ParseCurrentWeather(root);
        var (humidity, hourlyForecasts) = WeatherForecastParser.ParseHourlyForecast(root);
        var (highTempC, lowTempC, dailyForecasts) = WeatherForecastParser.ParseDailyForecast(root);
        return new WeatherSnapshot(
            tempC, feelsLikeC, humidity, windSpeedKmH, weatherCode, highTempC, lowTempC,
            dailyForecasts, hourlyForecasts, cityName, lat, lon, isDay);
    }

    /// <summary>
    /// Loads the disk cache and returns the stored snapshot (if any). The
    /// cache is identity-stamped at save (<see cref="WeatherCacheStore.SaveAsync"/>);
    /// a stamp that does not match <paramref name="location"/>'s query key is
    /// not applied — a cache written for a different resolution (a location
    /// edited after the last save) must never surface as fresh weather. An
    /// empty stamp is a legacy cache (predates the identity check) and applies
    /// as before.
    /// <para>
    /// STATE COMMITMENT: on success the load commits the cache's resolved
    /// identity (coordinates + name) into the resolution state and stamps
    /// the throttle to "now" — a freshly cached widget does not immediately
    /// re-fetch, matching the widget's boot semantics. The commit happens
    /// BEFORE the caller can decide what to do with the snapshot, so a caller
    /// that DISCARDS the result (a location change landing while the load was
    /// in flight) must roll the commitment back with
    /// <see cref="RollbackCacheLoad"/> — the interface says what the load did,
    /// so the rejection is the caller's job, never a silent side effect.
    /// </para>
    /// The token aborts the read on teardown, like every other fetch leg.
    /// </summary>
    public async Task<WeatherSnapshot?> LoadCacheAsync(WeatherLocation location, CancellationToken cancellationToken = default)
    {
        // The test seam: a substituted load leg (the boot race's file-free
        // drive). The state-commitment contract still applies to the
        // returned snapshot's identity: the client commits the cache's
        // resolved identity into the resolution state and stamps the throttle,
        // so a caller that DISCARDS the result must roll the commitment back.
        if (CacheLoadOverride is { } loadOverride)
        {
            WeatherSnapshot? snapshot = await loadOverride(location, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null && !TryApplyCacheIdentity(
                    WeatherQueryKey.Build(location), snapshot.Lat, snapshot.Lon, snapshot.ResolvedCityName, out _))
            {
                // The live identity no longer matches the payload's key — the
                // payload must not surface as fresh weather.
                return null;
            }
            return snapshot;
        }

        try
        {
            // The file format + bounded read live in the cache store; this
            // method owns only the semantics around the payload: whether the
            // identity stamp matches and the resolution-state apply.
            WeatherCachePayload? payload = await _cache.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (payload is null) return null;
            // The identity stamp: a cache saved for a different resolution
            // query must not be applied. An empty stamp (legacy cache) is
            // trusted — it predates the identity check.
            if (!string.IsNullOrEmpty(payload.LocationQueryKey)
                && !WeatherQueryKey.SameKey(payload.LocationQueryKey, WeatherQueryKey.Build(location)))
            {
                return null;
            }
            // A cache without a resolved name must not invent one — the naming
            // and the boot/conflict guard are the client's rules.
            // The identity fields are mutated UNDER the client's gate, and
            // only when no resolution for a DIFFERENT identity has started:
            // the boot load runs concurrently with the boot fetch, and a slow
            // load must not overwrite the coordinates/name a newer resolution
            // is producing (the fetch's guards validate the KEY — they cannot
            // see a state swap underneath it). Empty identity query = boot,
            // no resolution started yet — the legitimate load case.
            if (!TryApplyCacheIdentity(
                    WeatherQueryKey.Build(location), payload.Lat, payload.Lon, payload.ResolvedCityName, out string resolvedName))
            {
                return null;
            }
            return new WeatherSnapshot(
                payload.CurrentTempC, payload.FeelsLikeC, payload.Humidity, payload.WindSpeedKmH, payload.WeatherCode,
                payload.HighTempC, payload.LowTempC,
                // The store already capped the deserialized lists at the fetch
                // limits — a hand-edited or foreign cache cannot smuggle more
                // rows than the API ever returns.
                payload.DailyForecasts, payload.HourlyForecasts,
                resolvedName,
                payload.Lat ?? 0,
                payload.Lon ?? 0,
                payload.IsDay);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"Weather cache load failed: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>Deletes the disk cache (internal test seam — production never
    /// clears the cache at runtime).</summary>
    internal void ClearCache() => _cache.Clear();

    /// <summary>
    /// The test seam: substitutes the cache-load leg so the boot race
    /// (version + identity guards) is drivable without a file. When set,
    /// <see cref="LoadCacheAsync"/> routes through this delegate instead of
    /// the disk store; the state-commitment contract still applies to the
    /// returned snapshot's identity.
    /// </summary>
    internal Func<WeatherLocation, CancellationToken, Task<WeatherSnapshot?>>? CacheLoadOverride { get; set; }

    private async Task<WeatherResolutionOutcome> ResolveCoordinatesAsync(WeatherLocation location, string currentQuery, CancellationToken cancellationToken)
    {
        // The identity advances BEFORE the outcome is known. If the key
        // changed (a silent reassignment — hydration, or a direct property
        // write that bypassed OnPropertyChanged's invalidation — raced a
        // previous resolution), the client clears the OLD identity's
        // coordinates/name: a failed geocode for the new identity would
        // otherwise fall through with the previous place's lat/lon still set,
        // and the completion check (which compares against THIS new key)
        // would pass — fetching and caching the wrong city under the new
        // identity. Only a name resolution carries a population: the advance
        // resets it, and the resolution winner (the city leg or a "Location
        // Match" pick — the geocoder's door) sets the real value.

        // The ladder (explicit coordinates, a "lat,lon" pair, a postal code,
        // a "Location Match" pick, the city name) is the geocoder's single
        // resolution door; this method applies the verdict to client state,
        // never re-deriving the per-leg rules (the custom label's honor
        // rules, the dropdown refresh, never guessing a tie's coordinates).
        AdvanceResolution(currentQuery);

        var outcome = await _geocoder.ResolveAsync(location, _identity.Candidates, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case WeatherResolutionOutcome.Resolved r:
                SetResolved(r.Lat, r.Lon, r.Label, r.Population);
                // A geocode that produced candidates refreshes the dropdown; a
                // fast path (explicit/pair/ZIP/pick) leaves the last dropdown
                // untouched.
                if (r.RefreshedCandidates is { Count: > 0 })
                {
                    SetCandidates(r.RefreshedCandidates);
                }
                break;
            case WeatherResolutionOutcome.Ambiguous a:
                // Coordinates are never guessed for a tie; drop the stale
                // resolved name too — a previous resolution's name must never
                // trap the next editor with a place the fetch never reached.
                if (a.Candidates.Count > 0)
                {
                    SetCandidates(a.Candidates);
                }
                ClearCoordinates();
                break;
            case WeatherResolutionOutcome.Unresolved:
                // A failed geocode leaves the previous resolution valid.
                break;
        }

        // A geocode that resolves nothing stamps the attempt time so the
        // 5-minute throttle applies even without coordinates — a fetch will
        // never run for it, so it cannot stamp itself at completion
        // (ConfirmAndStamp). Without the stamp a typo'd city, an ambiguous tie,
        // or an outage would retry at render rate forever. The stamp is
        // identity-guarded like the fetch's catch block: a geocode that failed
        // AFTER the resolution identity changed must not cool down the NEW
        // identity's fetch (the caller's no-coordinates path reports Stale for
        // the same condition).
        if (outcome is WeatherResolutionOutcome.Ambiguous or WeatherResolutionOutcome.Unresolved)
        {
            Stamp(currentQuery);
        }
        return outcome;
    }

    /// <summary>
    /// The inspector's search-as-you-type surface: geocodes <paramref name="query"/>
    /// (a city name or a postal code) into ranked candidates with their exact
    /// coordinates and population. Returns an empty list on any failure — never
    /// throws; cancellation propagates so the editor can discard stale responses.
    /// The fetch + parse + candidate shaping live in <see cref="WeatherGeocoder"/>.
    /// </summary>
    public Task<IReadOnlyList<GeocodeCandidate>> SearchCitiesAsync(string query, CancellationToken cancellationToken = default)
        => _geocoder.SearchCitiesAsync(query, cancellationToken);

    /// <summary>
    /// The atomic claim + throttle gate: acquires the single-flight claim,
    /// then applies the throttle window unless forced. <see cref="BeginResult.InFlight"/>
    /// leaves the OTHER claim held (the caller does nothing); <see cref="BeginResult.Throttled"/>
    /// releases our claim before returning — the caller's finally must release
    /// only for <see cref="BeginResult.Started"/>.
    /// </summary>
    private BeginResult Begin(bool force)
    {
        if (Interlocked.CompareExchange(ref _claim, 1, 0) != 0) return BeginResult.InFlight;
        if (!force && (Clock.GetUtcNow().UtcDateTime - _lastFetchTime) < FetchWindow)
        {
            Interlocked.Exchange(ref _claim, 0);
            return BeginResult.Throttled;
        }
        return BeginResult.Started;
    }

    /// <summary>Releases the single-flight claim (the fetch's finally).</summary>
    private void End() => Interlocked.Exchange(ref _claim, 0);

    /// <summary>
    /// The single spelling of "the identity still matches the fetch's key":
    /// compares under the gate and, when it matches, stamps the throttle (an
    /// attempt cools down like a success). Returns whether the stamp was
    /// written — false means the identity changed mid-flight and the NEW
    /// identity's fetch must not be cooled down. The compare-and-stamp must
    /// be one gate section (an invalidation interleaved between a plain
    /// re-check and the stamp would write the OLD identity's throttle), so
    /// the transition keeps the ADR-0006 predicate under its own gate
    /// instead of routing through the capture window's plain re-check.
    /// Used by the failure path and the geocode leg; the success path uses
    /// <see cref="ConfirmAndStamp"/>, which also carries the resolved payload
    /// out under the same lock.
    /// </summary>
    private bool Stamp(string queryKey)
    {
        lock (_gate)
        {
            if (!WeatherQueryKey.SameKey(_lastLocationQuery, queryKey)) return false;
            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            return true;
        }
    }

    /// <summary>
    /// The success-path compare + stamp: confirms the identity still matches,
    /// stamps the throttle, and captures the resolved-identity payload
    /// (candidates, population) under the one gate — no invalidation can
    /// interleave and leave a stamp or payload for the OLD identity. Returns
    /// false (no stamp) when the identity changed mid-flight: the caller must
    /// report Stale, never apply or cache the snapshot.
    /// </summary>
    private bool ConfirmAndStamp(string queryKey, out IReadOnlyList<GeocodeCandidate> candidates, out double population)
    {
        lock (_gate)
        {
            if (!WeatherQueryKey.SameKey(_lastLocationQuery, queryKey))
            {
                candidates = [];
                population = 0;
                return false;
            }
            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            candidates = _identity.Candidates;
            population = _identity.Population;
            return true;
        }
    }

    /// <summary>
    /// Advances the resolution identity BEFORE the outcome is known. If the
    /// key changed (a silent reassignment — hydration, or a direct property
    /// write that bypassed invalidation — raced a previous resolution), the
    /// OLD identity's coordinates/name/population are cleared: a failed
    /// geocode for the new identity must not fall through with the previous
    /// place's state still set, and the completion check (which compares
    /// against THIS new key) would otherwise pass — fetching and caching the
    /// wrong city under the new identity. The geocode candidates SURVIVE the
    /// key change — they are cleared explicitly by the edit path's
    /// invalidation (the Location kind's whole-identity drop), because the
    /// LocationMatch edit's own drop resets the query to empty while KEEPING
    /// the candidates the pick resolves against: the pick's fetch then
    /// advances from empty and must still find its row (the geocoder's
    /// zero-HTTP fast path). The population reset rides the same lock so the
    /// fetch's next read is one consistent view.
    /// </summary>
    private void AdvanceResolution(string queryKey)
    {
        lock (_gate)
        {
            bool identityChanged = !WeatherQueryKey.SameKey(_lastLocationQuery, queryKey);
            _lastLocationQuery = queryKey;
            if (identityChanged)
            {
                _lat = null;
                _lon = null;
                _identity = _identity.With(resolvedName: "", population: 0);
            }
            else
            {
                _identity = _identity.With(population: 0);
            }
        }
    }

    /// <summary>Refreshes the "Location Match" dropdown's candidate list (a
    /// geocode that produced candidates; one that produced none leaves the
    /// last list untouched).</summary>
    private void SetCandidates(IReadOnlyList<GeocodeCandidate> candidates)
    {
        lock (_gate) { _identity = _identity.With(candidates: candidates); }
    }

    /// <summary>Applies a winning resolution: the exact coordinates, the
    /// composed label, and (for a name/pick resolution) the population.</summary>
    private void SetResolved(double lat, double lon, string name, double population)
    {
        lock (_gate)
        {
            _lat = lat;
            _lon = lon;
            _identity = _identity.With(resolvedName: name, population: population);
        }
    }

    /// <summary>Clears the coordinates and resolved name for an ambiguous tie —
    /// coordinates must never be guessed, and a previous resolution's name must
    /// not trap the next editor with a place the fetch never reached.</summary>
    private void ClearCoordinates()
    {
        lock (_gate)
        {
            _lat = null;
            _lon = null;
            _identity = _identity.With(resolvedName: "");
        }
    }

    /// <summary>
    /// Applies a cache payload's identity under the gate: a non-empty current
    /// query that differs from the payload's key means a different identity's
    /// resolution has started — the payload must not be applied (returns
    /// false). An empty current query is the boot case, where the load is
    /// legitimate. On apply, the resolved name comes from the payload's
    /// carried name, else the cached coordinates formatted, else the neutral
    /// label — never an invented city. The throttle is primed so a freshly
    /// cached widget does not immediately re-fetch.
    /// </summary>
    private bool TryApplyCacheIdentity(string queryKey, double? lat, double? lon, string? cachedName, out string appliedName)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_lastLocationQuery)
                && !WeatherQueryKey.SameKey(_lastLocationQuery, queryKey))
            {
                appliedName = "";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(cachedName))
            {
                appliedName = cachedName;
            }
            else if (lat is double cachedLat && lon is double cachedLon)
            {
                appliedName = WeatherLocationResolver.FormatCoordinates(cachedLat, cachedLon);
            }
            else
            {
                appliedName = _neutralLocationLabel;
            }
            _identity = _identity.With(resolvedName: appliedName);
            _lat = lat;
            _lon = lon;
            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            return true;
        }
    }
}

/// <summary>The outcome of <see cref="WeatherClient.Begin"/>.</summary>
internal enum BeginResult
{
    /// <summary>The claim was acquired; the caller runs the fetch and must
    /// call <see cref="WeatherClient.End"/> in a finally.</summary>
    Started,

    /// <summary>Another fetch is already in flight — nothing to do.</summary>
    InFlight,

    /// <summary>The throttle window has not elapsed; the attempt cools down
    /// like a success.</summary>
    Throttled,
}
