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

    private readonly WeatherGeocoder _geocoder;
    private readonly WeatherFetchControl _fetchControl;

    private readonly WeatherCacheStore _cache;
    private readonly Action<string, Exception?>? _logError;

    /// <summary>Test seam: injectable clock for fetch throttling (forwarded to
    /// the fetch-control module, which owns the throttle state).</summary>
    internal TimeProvider Clock
    {
        get => _fetchControl.Clock;
        set => _fetchControl.Clock = value;
    }

    private HttpClient? _testHttpClient;

    /// <summary>Test seam: substitute HTTP transport for fetch tests (defaults to <see cref="SharedHttpClient"/>).
    /// The geocoder follows the same seam, so both fetch legs are drivable from one property.</summary>
    internal HttpClient? TestHttpClient
    {
        get => _testHttpClient;
        set
        {
            _testHttpClient = value;
            _geocoder.Http = Http;
        }
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
    internal bool HasFetched => _fetchControl.HasFetched;

    /// <param name="cacheDirectory">Directory for the disk cache (created on demand).</param>
    /// <param name="cacheFileName">Per-instance cache file name; defaults to a shared "weather_default.json".</param>
    /// <param name="timeProvider">Test seam: clock for throttling and cache timestamps.</param>
    /// <param name="http">Test seam: substitute HTTP transport (defaults to the shared client).</param>
    /// <param name="logError">Optional error sink; when omitted, failures are silent.</param>
    public WeatherClient(string cacheDirectory, string? cacheFileName = null, TimeProvider? timeProvider = null, HttpClient? http = null, Action<string, Exception?>? logError = null)
        : this(cacheDirectory, () => cacheFileName ?? "weather_default.json", timeProvider, http, logError)
    {
    }

    /// <summary>
    /// Test/Internal seam: resolves the cache file name lazily, at each
    /// load/save, from a provider. A widget whose <c>InstanceId</c> is assigned
    /// after construction (RehydrateWidget sets it before InitializeAsync)
    /// must key its cache by that final identity — baking the name at
    /// construction would orphan every write under a never-reused GUID.
    /// </summary>
    internal WeatherClient(string cacheDirectory, Func<string> cacheFileNameProvider, TimeProvider? timeProvider = null, HttpClient? http = null, Action<string, Exception?>? logError = null)
    {
        _cache = new WeatherCacheStore(cacheDirectory, cacheFileNameProvider, logError);
        _fetchControl = new WeatherFetchControl(timeProvider ?? TimeProvider.System);
        _logError = logError;
        _geocoder = new WeatherGeocoder(SharedHttpClient, _logError);
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
    /// <see cref="WeatherFetchControl.FetchWindow"/> both this check and the
    /// atomic claim share — one spelling, drift impossible.
    /// </summary>
    internal bool IsFetchWindowElapsed() => _fetchControl.IsWindowElapsed();

    /// <summary>
    /// Resets resolved coordinates and the throttle so the next fetch
    /// re-resolves the location and runs immediately (location property change).
    /// Also drops the geocode candidates: a pick made against a previous
    /// location must never resolve against a changed Location/CountryCode/coords.
    /// </summary>
    internal void InvalidateLocation()
    {
        InvalidateCoordinates();
        _fetchControl.ClearCandidates();
    }

    /// <summary>
    /// Resets only the resolved coordinates and throttle — the geocode
    /// candidates stay. Used when the Location Match pick itself changes, so
    /// the pick can resolve against the candidates it was offered from. The
    /// resolved NAME is dropped too: it describes the previous resolution and
    /// must not render under a changed identity (a discarded cache load that
    /// applied state here rolls back through this same path).
    /// </summary>
    internal void InvalidateCoordinates() => _fetchControl.Invalidate();

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

        // The claim + throttle rules live in the fetch-control module: the
        // in-flight guard is Interlocked — the render tick, the refresh
        // timer, and OnTouch can race, and a check-then-set would let two of
        // them through. The claim's failure reason is reported so the caller
        // can tell "already being fetched" from "cooling down".
        var begin = _fetchControl.Begin(fetchQueryKey, force);
        if (begin == BeginResult.InFlight) return new WeatherFetchResult.InFlight();
        if (begin == BeginResult.Throttled) return new WeatherFetchResult.Throttled();

        try
        {
            if (!_fetchControl.Lat.HasValue || !_fetchControl.MatchesCurrent(fetchQueryKey) || force)
                await ResolveCoordinatesAsync(location, fetchQueryKey, cancellationToken).ConfigureAwait(false);

            if (!_fetchControl.Lat.HasValue || !_fetchControl.Lon.HasValue)
            {
                // No coordinates: the resolution failed or was left
                // unresolved. If the identity changed while it was in flight,
                // this is a STALE failure (the stale success path's verdict) —
                // the widget must re-fetch the new identity immediately, not
                // treat it as a plain failed attempt.
                if (!_fetchControl.MatchesCurrent(fetchQueryKey))
                {
                    return new WeatherFetchResult.Stale(fetchQueryKey);
                }
                return new WeatherFetchResult.Failed();
            }

            double lat = _fetchControl.Lat.Value;
            double lon = _fetchControl.Lon.Value;

            // The forecast leg: the URL's invariant F4 formatting lives in the
            // resolver behind the geocoder's door — a comma-decimal OS locale
            // must never interpolate "40,7100" into the query at a call site.
            string json = await _geocoder.ReadForecastAsync(lat, lon, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var (tempC, feelsLikeC, windSpeedKmH, weatherCode) = WeatherForecastParser.ParseCurrentWeather(root);
            var (humidity, hourlyForecasts) = WeatherForecastParser.ParseHourlyForecast(root);
            var (highTempC, lowTempC, dailyForecasts) = WeatherForecastParser.ParseDailyForecast(root);
            var snapshot = new WeatherSnapshot(
                tempC, feelsLikeC, humidity, windSpeedKmH, weatherCode, highTempC, lowTempC,
                dailyForecasts, hourlyForecasts, _fetchControl.ResolvedCityName, lat, lon);

            // The stale check: the widget invalidates the client (clearing
            // the identity query) when ANY resolution input changes. If that
            // happened while this fetch was in flight, the resolved identity
            // no longer matches the one this fetch started for — the snapshot
            // is stale: no throttle stamp (the new identity's fetch must not
            // cool down) and no cache write. ConfirmAndStamp compares, stamps,
            // and captures the resolved-identity payload under ONE gate, so a
            // concurrent invalidation cannot tear the comparison.
            if (!_fetchControl.ConfirmAndStamp(fetchQueryKey, out var candidates, out var population))
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
            if (!_fetchControl.MatchesCurrent(fetchQueryKey))
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
            return _fetchControl.Stamp(fetchQueryKey)
                ? new WeatherFetchResult.Failed()
                : new WeatherFetchResult.Stale(fetchQueryKey);
        }
        finally
        {
            _fetchControl.End();
            FetchCompletedCount++;
        }
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
    /// identity (coordinates + name) into the fetch-control state and stamps
    /// the throttle to "now" — a freshly cached widget does not immediately
    /// re-fetch, matching the widget's boot semantics. The commit happens
    /// BEFORE the caller can decide what to do with the snapshot, so a caller
    /// that DISCARDS the result (a location change landing while the load was
    /// in flight) must roll the commitment back with
    /// <see cref="InvalidateCoordinates"/> — the interface says what the load
    /// did, so the rejection is the caller's job, never a silent side effect.
    /// </para>
    /// The token aborts the read on teardown, like every other fetch leg.
    /// </summary>
    public async Task<WeatherSnapshot?> LoadCacheAsync(WeatherLocation location, CancellationToken cancellationToken = default)
    {
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
            // A cache without a resolved name must not invent one (the old
            // "New York" fallback mislabeled any location) — the naming and
            // the boot/conflict guard are the fetch-control module's rules.
            // The identity fields are mutated UNDER the module's gate, and
            // only when no resolution for a DIFFERENT identity has started:
            // the boot load runs concurrently with the boot fetch, and a slow
            // load must not overwrite the coordinates/name a newer resolution
            // is producing (the fetch's guards validate the KEY — they cannot
            // see a state swap underneath it). Empty identity query = boot,
            // no resolution started yet — the legitimate load case.
            if (!_fetchControl.TryApplyCacheIdentity(
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
                payload.Lon ?? 0);
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

    private async Task ResolveCoordinatesAsync(WeatherLocation location, string currentQuery, CancellationToken cancellationToken)
    {
        // The identity advances BEFORE the outcome is known. If the key
        // changed (a silent reassignment — hydration, or a direct property
        // write that bypasses OnPropertyChanged's invalidation — raced a
        // previous resolution), the module clears the OLD identity's
        // coordinates/name: a failed geocode for the new identity would
        // otherwise fall through with the previous place's lat/lon still set,
        // and the completion check (which compares against THIS new key)
        // would pass — fetching and caching the wrong city under the new
        // identity. Only a name resolution carries a population: the advance
        // resets it, and the resolution winner (the city leg or a "Location
        // Match" pick — the geocoder's door) sets the real value.

        // The ladder (explicit coordinates, a "lat,lon" pair, a postal code,
        // a "Location Match" pick, the city name) is the geocoder's single
        // resolution door; this method applies the verdict to module state,
        // never re-deriving the per-leg rules (the custom label's honor
        // rules, the dropdown refresh, never guessing a tie's coordinates).
        _fetchControl.AdvanceResolution(currentQuery);

        var outcome = await _geocoder.ResolveAsync(location, _fetchControl.Candidates, cancellationToken).ConfigureAwait(false);
        switch (outcome)
        {
            case WeatherResolutionOutcome.Resolved r:
                _fetchControl.SetResolved(r.Lat, r.Lon, r.Label, r.Population);
                // A geocode that produced candidates refreshes the dropdown; a
                // fast path (explicit/pair/ZIP/pick) leaves the last dropdown
                // untouched.
                if (r.RefreshedCandidates is { Count: > 0 })
                {
                    _fetchControl.SetCandidates(r.RefreshedCandidates);
                }
                break;
            case WeatherResolutionOutcome.Ambiguous a:
                // Coordinates are never guessed for a tie; drop the stale
                // resolved name too — a previous resolution's name must never
                // trap the next editor with a place the fetch never reached.
                if (a.Candidates.Count > 0)
                {
                    _fetchControl.SetCandidates(a.Candidates);
                }
                _fetchControl.ClearCoordinates();
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
            _fetchControl.Stamp(currentQuery);
        }
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
}
