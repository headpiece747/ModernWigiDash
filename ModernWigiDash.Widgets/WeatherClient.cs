using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>
/// A day-row of the daily forecast strip. Shared data model between
/// <see cref="WeatherClient"/> (producer) and the rendering widgets (consumer).
/// </summary>
internal readonly record struct DailyForecastItem(string DayName, double MaxTempC, double MinTempC, int WeatherCode);

/// <summary>
/// One column of the hourly forecast strip. Shared data model between
/// <see cref="WeatherClient"/> (producer) and the rendering widgets (consumer).
/// </summary>
internal readonly record struct HourlyForecastItem(string TimeLabel, double TempC, int WeatherCode);

/// <summary>
/// A resolved place the weather is fetched for. Latitude/Longitude are the
/// optional explicit coordinate overrides; when either is empty the location
/// query (city name, ZIP, or "lat,lon" pair) is resolved via geocoding.
/// <see cref="CountryCode"/> is the optional ISO country-code hint ("US",
/// "DE", ...) that disambiguates same-named cities across countries.
/// </summary>
internal sealed record WeatherLocation(string LocationType, string Location, string? Latitude, string? Longitude, string? CustomLabel)
{
    /// <summary>Optional ISO 3166-1 alpha-2 country-code hint for geocoding.</summary>
    public string? CountryCode { get; init; }

    /// <summary>
    /// Optional user pick from the geocoder's candidates ("Location Match"
    /// dropdown): the chosen candidate's label, resolved directly to its
    /// coordinates instead of re-geocoding.
    /// </summary>
    public string? LocationMatch { get; init; }

    public WeatherLocation(string locationType, string location, string? latitude, string? longitude, string? customLabel, string? countryCode)
        : this(locationType, location, latitude, longitude, customLabel) => CountryCode = countryCode;
}

/// <summary>
/// One complete Open-Meteo fetch result. Fields are null when the response
/// omitted that section — consumers keep their previous value in that case.
/// </summary>
internal sealed record WeatherSnapshot(
    double? CurrentTempC,
    double? FeelsLikeC,
    double? Humidity,
    double? WindSpeedKmH,
    int? WeatherCode,
    double? HighTempC,
    double? LowTempC,
    IReadOnlyList<DailyForecastItem>? DailyForecasts,
    IReadOnlyList<HourlyForecastItem>? HourlyForecasts,
    string ResolvedCityName,
    double Lat,
    double Lon);

/// <summary>
/// The outcome of one <see cref="WeatherClient.FetchCurrentAsync"/> call. The
/// union shapes make "a result with no snapshot" unrepresentable: the snapshot
/// exists only on <see cref="Fetched"/>.
/// </summary>
internal abstract record WeatherFetchResult
{
    /// <summary>A fresh snapshot was produced and the fetch completed; the
    /// caller applies it (and may write back the resolved label). Carries the
    /// resolved identity the snapshot was fetched for: the geocode candidates
    /// (the widget's "Location Match" dropdown) and the winner's population,
    /// so the widget can store its own copies instead of re-reading the
    /// client's resolution state. The resolved display name rides the
    /// snapshot itself (one label source).</summary>
    public sealed record Fetched(
        WeatherSnapshot Snapshot,
        IReadOnlyList<GeocodeCandidate> Candidates,
        double Population) : WeatherFetchResult;

    /// <summary>The throttle window was open; no request was made and the
    /// caller keeps its previous state.</summary>
    public sealed record Throttled : WeatherFetchResult;

    /// <summary>Another fetch was already in flight; no request was made and
    /// the caller keeps its previous state (the in-flight fetch applies when
    /// it completes).</summary>
    public sealed record InFlight : WeatherFetchResult;

    /// <summary>The request failed or the location could not be resolved; no
    /// snapshot and the caller keeps its previous state.</summary>
    public sealed record Failed : WeatherFetchResult;

    /// <summary>The resolution identity was invalidated while the fetch was in
    /// flight; the snapshot is stale and must not be applied, and no throttle
    /// stamp was written (the caller re-fetches the new identity immediately).</summary>
    public sealed record Stale : WeatherFetchResult;
}

/// <summary>
/// One geocoding candidate the user can pick from the widget's "Location
/// Match" dropdown: the display label (Name, Admin1, Country) and the exact
/// coordinates it resolves to. When the user picks one, the widget re-fetches
/// with its query so the pick is honored deterministically.
/// </summary>
public sealed record GeocodeCandidate(string Label, string Query, double Lat, double Lon)
{
    /// <summary>Candidate population (the search list's disambiguating label
    /// data; 0 when the geocoder omitted it).</summary>
    public double Population { get; init; }
}

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

    /// <summary>The fetch throttle window — the one spelling shared by the
    /// atomic claim in <see cref="FetchCurrentAsync"/>, the render-tick
    /// pre-check (<see cref="IsFetchWindowElapsed"/>), and the widget's
    /// hidden-page refresh loop; a change edits one constant.</summary>
    internal static readonly TimeSpan FetchWindow = TimeSpan.FromMinutes(5);

    /// <summary>The maximum daily forecast rows the client ever keeps — the
    /// parse cap, the deserialized-cache cap, and the API's own response
    /// length share this one limit.</summary>
    internal const int MaxFetchDays = 7;

    /// <summary>The maximum hourly forecast rows the client ever keeps — the
    /// parse cap, the deserialized-cache cap, and the API's own response
    /// length share this one limit.</summary>
    internal const int MaxFetchHours = 12;

    /// <summary>The disk-cache size bound: a cache file larger than this is
    /// rejected before reading (a corrupted or foreign file must never be
    /// buffered into memory).</summary>
    internal const long MaxCacheBytes = 1024 * 1024;

    /// <summary>The neutral resolved-name label when no resolution exists
    /// (one spelling shared by the client and the widget's copy).</summary>
    internal const string UnknownLocationLabel = "Unknown location";

    private readonly string _cacheDirectory;
    private readonly Func<string> _cacheNameProvider;
    private readonly Action<string, Exception?>? _logError;

    private DateTime _lastFetchTime = DateTime.MinValue;
    private int _fetchClaim; // 1 = a fetch is in flight (see TryBeginFetch)
    private string _lastLocationQuery = "";

    /// <summary>Serializes the identity fields (_lastLocationQuery,
    /// _lastFetchTime, _lat/_lon) between the fetch continuation (thread pool)
    /// and the widget's invalidation calls (UI thread).</summary>
    private readonly Lock _identityGate = new();

    private double? _lat;
    private double? _lon;
    // Neutral until a resolution sets a real identity (never a hardcoded city).
    private string _resolvedCityName = UnknownLocationLabel;

    /// <summary>Test seam: injectable clock for fetch throttling and cache timestamps.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

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

    /// <summary>
    /// Number of completed fetches (success or failure) — a test seam for
    /// waiting on fetch completion, since the in-flight claim releases only
    /// when <see cref="FetchCurrentAsync"/> returns.
    /// </summary>
    internal int FetchCompletedCount { get; private set; }

    /// <summary>
    /// The geocode candidates from the last city-name resolution, in API order
    /// — the widget's "Location Match" dropdown options. Empty when the last
    /// resolution was coordinates, a ZIP, or a failed geocode.
    /// </summary>
    internal IReadOnlyList<GeocodeCandidate> LastCandidates { get; private set; } = [];

    /// <summary>The last resolved winner's population (0 when the resolution had
    /// no population, e.g. ZIP/coordinate paths or an ambiguous tie).</summary>
    internal double LastResolvedPopulation { get; private set; }

    /// <summary>UTC timestamp of the last successful fetch or cache load (drives throttling).</summary>
    internal DateTime LastFetchTimeUtc => _lastFetchTime;

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
        _cacheDirectory = cacheDirectory;
        _cacheNameProvider = cacheFileNameProvider;
        Clock = timeProvider ?? TimeProvider.System;
        _logError = logError;
        _geocoder = new WeatherGeocoder(SharedHttpClient, _logError);
        TestHttpClient = http;
        Directory.CreateDirectory(cacheDirectory);
    }

    /// <summary>The current cache file path, derived from the live name provider.</summary>
    private string CachePath => Path.Combine(_cacheDirectory, _cacheNameProvider());

    /// <summary>The cache file name the provider currently resolves (test seam).</summary>
    internal string CacheFileName => _cacheNameProvider();

    private void EndFetch() => Interlocked.Exchange(ref _fetchClaim, 0);

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
    /// Resets resolved coordinates and the throttle so the next fetch
    /// re-resolves the location and runs immediately (location property change).
    /// Also drops the geocode candidates: a pick made against a previous
    /// location must never resolve against a changed Location/CountryCode/coords.
    /// </summary>
    internal void InvalidateLocation()
    {
        InvalidateCoordinates();
        LastCandidates = [];
    }

    /// <summary>
    /// Resets only the resolved coordinates and throttle — the geocode
    /// candidates stay. Used when the Location Match pick itself changes, so
    /// the pick can resolve against the candidates it was offered from. The
    /// resolved NAME is dropped too: it describes the previous resolution and
    /// must not render under a changed identity (a discarded cache load that
    /// applied state here rolls back through this same path).
    /// </summary>
    internal void InvalidateCoordinates()
    {
        lock (_identityGate)
        {
            _lat = null;
            _lon = null;
            _resolvedCityName = "";
            _lastFetchTime = DateTime.MinValue;
            _lastLocationQuery = "";
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
        string fetchQueryKey = BuildQueryKey(location);

        // The in-flight guard is Interlocked — the render tick, the refresh
        // timer, and OnTouch can race, and a check-then-set would let two of
        // them through. The claim's failure reason is reported so the caller
        // can tell "already being fetched" from "cooling down".
        if (Interlocked.CompareExchange(ref _fetchClaim, 1, 0) != 0)
        {
            return new WeatherFetchResult.InFlight();
        }
        if (!force && (Clock.GetUtcNow().UtcDateTime - _lastFetchTime) < FetchWindow)
        {
            Interlocked.Exchange(ref _fetchClaim, 0);
            return new WeatherFetchResult.Throttled();
        }

        try
        {
            if (!_lat.HasValue || !string.Equals(_lastLocationQuery, fetchQueryKey, StringComparison.Ordinal) || force)
                await ResolveCoordinatesAsync(location, fetchQueryKey, cancellationToken).ConfigureAwait(false);

            if (!_lat.HasValue || !_lon.HasValue)
            {
                // No coordinates: the resolution failed or was left
                // unresolved. If the identity changed while it was in flight,
                // this is a STALE failure (the stale success path's verdict) —
                // the widget must re-fetch the new identity immediately, not
                // treat it as a plain failed attempt.
                lock (_identityGate)
                {
                    if (!string.Equals(_lastLocationQuery, fetchQueryKey, StringComparison.Ordinal))
                    {
                        return new WeatherFetchResult.Stale();
                    }
                }
                return new WeatherFetchResult.Failed();
            }

            double lat = _lat.Value;
            double lon = _lon.Value;

            // The forecast URL is built in WeatherLocationResolver with
            // invariant F4 formatting — a comma-decimal OS locale must never
            // interpolate "40,7100" into the query.
            string forecastUrl = WeatherLocationResolver.BuildForecastUri(lat, lon).ToString();
            string json = await WeatherGeocoder.ReadBoundedAsync(Http, forecastUrl, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var (tempC, feelsLikeC, windSpeedKmH, weatherCode) = ParseCurrentWeather(root);
            var (humidity, hourlyForecasts) = ParseHourlyForecast(root);
            var (highTempC, lowTempC, dailyForecasts) = ParseDailyForecast(root);
            var snapshot = new WeatherSnapshot(
                tempC, feelsLikeC, humidity, windSpeedKmH, weatherCode, highTempC, lowTempC,
                dailyForecasts, hourlyForecasts, _resolvedCityName, lat, lon);

            // The stale check: the widget invalidates the client (clearing
            // _lastLocationQuery) when ANY resolution input changes. If that
            // happened while this fetch was in flight, the resolved identity
            // no longer matches the one this fetch started for — the snapshot
            // is stale: no throttle stamp (the new identity's fetch must not
            // cool down) and no cache write. The identity fields are read and
            // cleared under one gate so a concurrent invalidation cannot tear
            // the comparison.
            WeatherFetchResult fetched;
            lock (_identityGate)
            {
                if (!string.Equals(_lastLocationQuery, fetchQueryKey, StringComparison.Ordinal))
                {
                    return new WeatherFetchResult.Stale();
                }
                // The throttle stamp and the resolved-identity payload ride
                // the same lock as the confirmation: no invalidation can
                // interleave and leave a stamp or payload for the OLD
                // identity (the widget copies these into its own state).
                _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
                fetched = new WeatherFetchResult.Fetched(snapshot, LastCandidates, LastResolvedPopulation);
            }

            await SaveCacheAsync(snapshot, fetchQueryKey, cancellationToken).ConfigureAwait(false);
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
            return TryStampForIdentity(fetchQueryKey)
                ? new WeatherFetchResult.Failed()
                : new WeatherFetchResult.Stale();
        }
        finally
        {
            EndFetch();
            FetchCompletedCount++;
        }
    }

    /// <summary>
    /// The single spelling of "the identity still matches the fetch's key":
    /// compares under the identity gate and, when it matches, stamps the
    /// throttle (an attempt cools down like a success). Returns whether the
    /// stamp was written — false means the identity changed mid-flight and
    /// the NEW identity's fetch must not be cooled down. Used by the fetch's
    /// failure path and the geocode leg; the success path keeps its inline
    /// version because the stamp, the compare, and the Fetched payload
    /// construction must share one lock.
    /// </summary>
    private bool TryStampForIdentity(string fetchQueryKey)
    {
        lock (_identityGate)
        {
            if (!string.Equals(_lastLocationQuery, fetchQueryKey, StringComparison.Ordinal)) return false;
            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            return true;
        }
    }

    /// <summary>
    /// Loads the disk cache and returns the stored snapshot (if any). The
    /// cache is identity-stamped at save (<see cref="WeatherCacheData.LocationQueryKey"/>);
    /// a stamp that does not match <paramref name="location"/>'s query key is
    /// not applied — a cache written for a different resolution (a location
    /// edited after the last save) must never surface as fresh weather. An
    /// empty stamp is a legacy cache (predates the identity check) and applies
    /// as before. On success the fetch throttle is primed to "now" so a
    /// freshly cached widget does not immediately re-fetch — matching the
    /// widget's boot semantics. The token aborts the read on teardown, like
    /// every other fetch leg.
    /// </summary>
    public async Task<WeatherSnapshot?> LoadCacheAsync(WeatherLocation location, CancellationToken cancellationToken = default)
    {
        try
        {
            string resolvedName = UnknownLocationLabel;
            string path = CachePath;
            if (!File.Exists(path)) return null;
            // A cache file larger than the bound is a corrupted/foreign file —
            // reject it before reading a byte. The read itself is BOUNDED
            // (streamed with a hard cap): a stat-then-ReadAllText would let a
            // file that grows between the stat and the read bypass the cap
            // and buffer unboundedly.
            string? json = await ReadCacheFileBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            if (json is null) return null;
            var data = JsonSerializer.Deserialize<WeatherCacheData>(json);
            if (data == null) return null;
            // The identity stamp: a cache saved for a different resolution
            // query must not be applied. An empty stamp (legacy cache) is
            // trusted — it predates the identity check.
            if (!string.IsNullOrEmpty(data.LocationQueryKey)
                && !string.Equals(data.LocationQueryKey, BuildQueryKey(location), StringComparison.Ordinal))
            {
                return null;
            }
            // A cache without a resolved name must not invent one (the old
            // "New York" fallback mislabeled any location) — use the cached
            // coordinates, the only truthful identity the cache carries. The
            // identity fields are mutated UNDER the gate, and only when no
            // resolution for a DIFFERENT identity has started: the boot load
            // runs concurrently with the boot fetch, and a slow load must not
            // overwrite the coordinates/name a newer resolution is producing
            // (the fetch's guards validate the KEY — they cannot see a state
            // swap underneath it). Empty _lastLocationQuery = boot, no
            // resolution started yet — the legitimate load case.
            lock (_identityGate)
            {
                if (!string.IsNullOrEmpty(_lastLocationQuery)
                    && !string.Equals(_lastLocationQuery, BuildQueryKey(location), StringComparison.Ordinal))
                {
                    return null;
                }
                if (!string.IsNullOrWhiteSpace(data.ResolvedCityName))
                {
                    _resolvedCityName = data.ResolvedCityName;
                }
                else if (data.Lat is double cachedLat && data.Lon is double cachedLon)
                {
                    _resolvedCityName = WeatherGeocoder.FormatCoordinates(cachedLat, cachedLon);
                }
                else
                {
                    _resolvedCityName = UnknownLocationLabel;
                }
                _lat = data.Lat;
                _lon = data.Lon;
                _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
                // Captured under the gate: the snapshot below must carry the
                // name consistent with the state just applied, not whatever a
                // concurrent resolution produced after the lock released.
                resolvedName = _resolvedCityName;
            }
            return new WeatherSnapshot(
                data.CurrentTempC,
                data.FeelsLikeC,
                data.Humidity,
                data.WindSpeedKmH,
                data.WeatherCode,
                data.HighTempC,
                data.LowTempC,
                // Deserialized lists are capped at the fetch limits — a
                // hand-edited or foreign cache cannot smuggle more rows than
                // the API ever returns.
                (data.DailyForecasts ?? []).Take(MaxFetchDays).Select(d => new DailyForecastItem(d.DayName, d.MaxTempC, d.MinTempC, d.WeatherCode)).ToArray(),
                (data.HourlyForecasts ?? []).Take(MaxFetchHours).Select(h => new HourlyForecastItem(h.TimeLabel, h.TempC, h.WeatherCode)).ToArray(),
                resolvedName,
                data.Lat ?? 0,
                data.Lon ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"Weather cache load failed: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Reads the cache file with a HARD byte cap — the stat-then-read gap is
    /// closed: a file that grows (or is swapped) after the existence check is
    /// still truncated at <see cref="MaxCacheBytes"/> instead of buffered
    /// whole. Returns null (logged) when the file exceeds the cap mid-read
    /// (the loop stops at the cap, so a file larger than the cap is detected
    /// by the total read falling short of the file's length).
    /// </summary>
    private async Task<string?> ReadCacheFileBoundedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            // The stream tolerates a concurrent writer (ReadWrite share): the
            // bounded read + the length guards below make mid-read growth
            // safe-by-rejection — a FileShare.Read open would instead fail at
            // open with a sharing violation the moment another process writes
            // the cache, never reaching the guards that own the decision.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length > MaxCacheBytes)
            {
                _logError?.Invoke($"Weather cache load failed: cache file exceeds the {MaxCacheBytes} byte bound", null);
                return null;
            }
            byte[] chunk = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                // The capacity hint must never exceed the read bound: the file
                // can grow between the length check and this allocation (the
                // stream tolerates a concurrent writer), so an attacker-
                // influenced length must not size the buffer — the read loop
                // caps at MaxCacheBytes anyway.
                using var buffer = new MemoryStream((int)Math.Min(fs.Length, MaxCacheBytes));
                long total = 0;
                while (total < MaxCacheBytes)
                {
                    int remaining = (int)Math.Min(chunk.Length, MaxCacheBytes - total);
                    int read = await fs.ReadAsync(chunk.AsMemory(0, remaining), cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (total < fs.Length)
                {
                    _logError?.Invoke($"Weather cache load failed: cache file exceeds the {MaxCacheBytes} byte bound", null);
                    return null;
                }
                return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"Weather cache load failed: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>Deletes the disk cache (internal test seam — production never
    /// clears the cache at runtime).</summary>
    internal void ClearCache()
    {
        try
        {
            if (File.Exists(CachePath)) File.Delete(CachePath);
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Weather cache clear failed: {ex.Message}", ex);
        }
    }

    private async Task SaveCacheAsync(WeatherSnapshot snapshot, string queryKey, CancellationToken cancellationToken)
    {
        try
        {
            var data = new WeatherCacheData
            {
                CurrentTempC = snapshot.CurrentTempC ?? 0,
                FeelsLikeC = snapshot.FeelsLikeC ?? 0,
                Humidity = snapshot.Humidity ?? 0,
                WindSpeedKmH = snapshot.WindSpeedKmH ?? 0,
                WeatherCode = snapshot.WeatherCode ?? 0,
                HighTempC = snapshot.HighTempC ?? 0,
                LowTempC = snapshot.LowTempC ?? 0,
                ResolvedCityName = snapshot.ResolvedCityName,
                Lat = snapshot.Lat,
                Lon = snapshot.Lon,
                // The identity stamp: the query key this snapshot was fetched
                // for. LoadCacheAsync applies the cache only when the stamp
                // matches the loading location's key (or is empty = legacy).
                LocationQueryKey = queryKey,
                DailyForecasts = (snapshot.DailyForecasts ?? []).Select(d => new DailyForecastData { DayName = d.DayName, MaxTempC = d.MaxTempC, MinTempC = d.MinTempC, WeatherCode = d.WeatherCode }).ToList(),
                HourlyForecasts = (snapshot.HourlyForecasts ?? []).Select(h => new HourlyForecastData { TimeLabel = h.TimeLabel, TempC = h.TempC, WeatherCode = h.WeatherCode }).ToList()
            };
            string json = JsonSerializer.Serialize(data);
            // Atomic write: the temp file is written fully, then moved over the
            // target. A crash mid-write can never leave a truncated cache that
            // the next boot reads as a fresh snapshot. The temp name is unique
            // per save — a fixed "<name>.tmp" would interleave two concurrent
            // writers (e.g. two app instances) and let a torn file win the move.
            string path = CachePath;
            string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, path, overwrite: true);
            }
            finally
            {
                // Best-effort: a crash between write and move (or a locked
                // target) must not accumulate orphan temp files.
                try { File.Delete(tempPath); } catch { /* best-effort cleanup */ }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"Weather cache save failed: {ex.Message}", ex);
        }
    }

    private async Task ResolveCoordinatesAsync(WeatherLocation location, string currentQuery, CancellationToken cancellationToken)
    {
        // The identity advances BEFORE the outcome is known. If the key
        // changed (a silent reassignment — hydration, or a direct property
        // write that bypasses OnPropertyChanged's invalidation — raced a
        // previous resolution), the OLD identity's coordinates/name must not
        // survive: a failed geocode for the new identity would otherwise fall
        // through with the previous place's lat/lon still set, and the
        // completion check (which compares against THIS new key) would pass —
        // fetching and caching the wrong city under the new identity.
        bool identityChanged;
        lock (_identityGate)
        {
            identityChanged = !string.Equals(_lastLocationQuery, currentQuery, StringComparison.Ordinal);
            _lastLocationQuery = currentQuery;
            if (identityChanged)
            {
                _lat = null;
                _lon = null;
                _resolvedCityName = "";
            }
        }

        // Only a name resolution carries a population: explicit coordinates, a
        // coordinate pair, and a ZIP path reset it; the city-resolution winner
        // and pick paths below set the real value.
        LastResolvedPopulation = 0;

        // Explicit coordinates are authoritative — they must win over a stale
        // Location Match pick from a previous city query. The pair is only
        // honored when BOTH values are usable coordinates: "NaN"/"Infinity"
        // parse as doubles, so the range check is what rejects them (and the
        // resolution falls back to the location query instead of poisoning
        // the forecast URL).
        if (double.TryParse(location.Latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var latVal)
            && double.TryParse(location.Longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lonVal)
            && WeatherGeocoder.IsValidCoordinate(latVal, lonVal))
        {
            _lat = latVal;
            _lon = lonVal;
            _resolvedCityName = string.IsNullOrWhiteSpace(location.CustomLabel)
                ? WeatherGeocoder.FormatCoordinates(latVal, lonVal)
                : location.CustomLabel;
        }
        else if (WeatherGeocoder.TryParseCoordinatePair(location.Location, out double pairLat, out double pairLon))
        {
            _lat = pairLat;
            _lon = pairLon;
            _resolvedCityName = WeatherGeocoder.FormatCoordinates(pairLat, pairLon);
        }
        else if (WeatherLocationResolver.IsZipCode(location.Location))
        {
            // The ZIP path routes by the country hint (zippopotam /de/, /us/,
            // ...); unsupported countries 404 and fall back to the geocoder.
            var zip = await _geocoder.GeocodeZipAsync(location.Location, location.CountryCode, cancellationToken).ConfigureAwait(false);
            if (zip is not null)
            {
                _lat = zip.Lat;
                _lon = zip.Lon;
                _resolvedCityName = zip.CityName;
                return;
            }

            // The zippopotam path is US-only; fall back to the worldwide
            // Open-Meteo geocoder WITH the original location (so the
            // CountryCode hint is carried — e.g. "10115" + "DE" resolves the
            // Berlin postal district).
            await ResolveCityAsync(location, currentQuery, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // A user pick from the "Location Match" dropdown resolves directly
            // to that candidate's exact coordinates — no re-geocode. The pick
            // is only honored inside the city branch (after the override and
            // ZIP paths), and candidates were cleared by InvalidateLocation on
            // any location/coords change, so a stale pick cannot win.
            if (!string.IsNullOrWhiteSpace(location.LocationMatch))
            {
                var match = LastCandidates.FirstOrDefault(c =>
                    c.Query.Equals(location.LocationMatch.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    _lat = match.Lat;
                    _lon = match.Lon;
                    _resolvedCityName = string.IsNullOrWhiteSpace(location.CustomLabel) ? match.Label : location.CustomLabel;
                    LastResolvedPopulation = match.Population;
                    return;
                }
            }

            await ResolveCityAsync(location, currentQuery, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The city-name resolution leg: geocodes via <see cref="WeatherGeocoder"/>
    /// and applies the resolved state — the winner's coordinates, label, and
    /// population; an ambiguous tie clears the resolution (the "Location
    /// Match" dropdown stays populated with the tie's candidates); a failed
    /// geocode leaves the previous resolution valid. Every non-resolved
    /// outcome stamps the attempt time so the 5-minute throttle applies even
    /// without coordinates — otherwise a typo'd city, an ambiguous tie, or an
    /// outage would retry at render rate forever.
    /// </summary>
    private async Task ResolveCityAsync(WeatherLocation location, string fetchQueryKey, CancellationToken cancellationToken)
    {
        var result = await _geocoder.GeocodeCityAsync(location.Location, location.CountryCode, location.LocationMatch, cancellationToken).ConfigureAwait(false);

        // A geocode that produced candidates refreshes the dropdown; one that
        // produced none (failure or an empty response) leaves the last
        // dropdown untouched.
        if (result.Candidates.Count > 0) LastCandidates = result.Candidates;

        if (result is WeatherCityGeocodeResult.Resolved r)
        {
            _lat = r.Lat;
            _lon = r.Lon;
            _resolvedCityName = r.Label;
            LastResolvedPopulation = r.Population;
            return;
        }

        if (result is WeatherCityGeocodeResult.Ambiguous)
        {
            _lat = null;
            _lon = null;
            // Drop the stale resolved name too: a previous resolution's
            // name must never trap the next editor with a place the fetch
            // never reached.
            _resolvedCityName = "";
        }

        // A failed or ambiguous geocode leaves the coordinates unresolved:
        // FetchCurrentAsync returns a Failed outcome (no snapshot) and the
        // widget renders its "no data" state instead of silently pinning a
        // default location. The throttle stamp is identity-guarded like the
        // fetch's catch block: a geocode that failed AFTER the resolution
        // identity changed must not cool down the NEW identity's fetch (the
        // caller's no-coordinates path reports Stale for the same condition).
        TryStampForIdentity(fetchQueryKey);
    }

    /// <summary>
    /// Parses current conditions. The modern <c>current</c> block (15-minute
    /// precision, humidity + feels-like included) is primary; the legacy
    /// <c>current_weather</c> block is the fallback so cached/legacy responses
    /// still parse. The legacy block never carried apparent_temperature or
    /// humidity, which is why this reads them from <c>current</c>.
    /// </summary>
    private static (double? TempC, double? FeelsLikeC, double? WindSpeedKmH, int? WeatherCode) ParseCurrentWeather(JsonElement root)
    {
        double? tempC = null;
        double? feelsLikeC = null;
        double? windSpeedKmH = null;
        int? weatherCode = null;

        if (root.TryGetProperty("current", out var current))
        {
            if (current.TryGetProperty("temperature_2m", out var tempEl))
                tempC = tempEl.GetDouble();
            if (current.TryGetProperty("apparent_temperature", out var feelsEl))
                feelsLikeC = feelsEl.GetDouble();
            if (current.TryGetProperty("wind_speed_10m", out var windEl))
                windSpeedKmH = windEl.GetDouble();
            if (current.TryGetProperty("weather_code", out var codeEl))
                weatherCode = codeEl.GetInt32();
            return (tempC, feelsLikeC, windSpeedKmH, weatherCode);
        }

        if (!root.TryGetProperty("current_weather", out var currentWeather)) return (null, null, null, null);

        if (currentWeather.TryGetProperty("temperature", out var legacyTemp))
            tempC = legacyTemp.GetDouble();
        // The legacy block has no apparent_temperature — the URL's
        // apparent_temperature=true hint never materialized there, so the
        // "feels like" metric can only come from the modern current block.
        if (currentWeather.TryGetProperty("apparent_temperature", out var legacyFeels))
            feelsLikeC = legacyFeels.GetDouble();
        if (currentWeather.TryGetProperty("windspeed", out var legacyWind))
            windSpeedKmH = legacyWind.GetDouble();
        if (currentWeather.TryGetProperty("weathercode", out var legacyCode))
            weatherCode = legacyCode.GetInt32();

        return (tempC, feelsLikeC, windSpeedKmH, weatherCode);
    }

    private static (double? Humidity, IReadOnlyList<HourlyForecastItem>? Hourly) ParseHourlyForecast(JsonElement root)
    {
        // Humidity is a current condition — the modern current block carries
        // it at 15-minute precision. The hourly array starts at local midnight,
        // so its first bucket is hours stale; never use it as "current".
        if (root.TryGetProperty("current", out var current)
            && current.TryGetProperty("relative_humidity_2m", out var humEl))
        {
            double? humidity = humEl.GetDouble();

            IReadOnlyList<HourlyForecastItem>? hourlyForecasts = ParseHourlyItems(root);
            return (humidity, hourlyForecasts);
        }

        if (!root.TryGetProperty("hourly", out var hourly)
            || !hourly.TryGetProperty("temperature_2m", out var temps)
            || temps.GetArrayLength() <= 0) return (null, null);

        // Legacy fallback: no current block, so the hourly array is the only
        // humidity source (still stale-by-hours — better than nothing).
        double? legacyHumidity = null;
        if (hourly.TryGetProperty("relativehumidity_2m", out var hums) && hums.GetArrayLength() > 0)
            legacyHumidity = hums[0].GetDouble();

        return (legacyHumidity, ParseHourlyItems(root));
    }

    private static IReadOnlyList<HourlyForecastItem>? ParseHourlyItems(JsonElement root)
    {
        if (!root.TryGetProperty("hourly", out var hourly)) return null;
        if (!hourly.TryGetProperty("time", out var times)
            || !hourly.TryGetProperty("temperature_2m", out var tempsInner)
            || times.GetArrayLength() <= 0 || tempsInner.GetArrayLength() <= 0) return null;

        // Both the modern (weather_code) and legacy (weathercode) names are
        // honored so either response shape parses.
        bool modernCodes = hourly.TryGetProperty("weather_code", out var codes);
        bool legacyCodes = !modernCodes && hourly.TryGetProperty("weathercode", out codes);

        int hLen = Math.Min(times.GetArrayLength(), tempsInner.GetArrayLength());
        List<HourlyForecastItem> items = [];
        for (int i = 0; i < Math.Min(hLen, MaxFetchHours); i++)
        {
            string timeStr = times[i].GetString() ?? "";
            string label = timeStr.Length >= 16 ? timeStr[11..16] : $"{i}:00";
            int code = modernCodes || legacyCodes ? codes[i].GetInt32() : 0;
            items.Add(new HourlyForecastItem(label, tempsInner[i].GetDouble(), code));
        }
        return items;
    }

    private static (double? HighTempC, double? LowTempC, IReadOnlyList<DailyForecastItem>? Daily) ParseDailyForecast(JsonElement root)
    {
        if (!root.TryGetProperty("daily", out var daily)) return (null, null, null);

        double? highTempC = null;
        double? lowTempC = null;
        if (daily.TryGetProperty("temperature_2m_max", out var maxes) && maxes.GetArrayLength() > 0)
            highTempC = maxes[0].GetDouble();
        if (daily.TryGetProperty("temperature_2m_min", out var mins) && mins.GetArrayLength() > 0)
            lowTempC = mins[0].GetDouble();

        IReadOnlyList<DailyForecastItem>? dailyForecasts = null;
        if (daily.TryGetProperty("time", out var dTimes) && daily.TryGetProperty("temperature_2m_max", out maxes))
        {
            // Both the modern (weather_code) and legacy (weathercode) names are
            // honored so either response shape parses.
            bool modernCodes = daily.TryGetProperty("weather_code", out var dCodes);
            bool legacyCodes = !modernCodes && daily.TryGetProperty("weathercode", out dCodes);
            if (modernCodes || legacyCodes)
            {
                dailyForecasts = BuildDailyItems(dTimes, maxes, mins, dCodes);
            }
        }
        return (highTempC, lowTempC, dailyForecasts);
    }

    private static List<DailyForecastItem> BuildDailyItems(JsonElement dTimes, JsonElement maxes, JsonElement mins, JsonElement codes)
    {
        int dLen = Math.Min(dTimes.GetArrayLength(), maxes.GetArrayLength());
        List<DailyForecastItem> items = [];
        for (int i = 0; i < Math.Min(dLen, MaxFetchDays); i++)
        {
            string dateStr = dTimes[i].GetString() ?? "";
            // Day names render in the INVARIANT calendar (the strip's "Today"
            // marker is English by design) — DayOfWeek.ToString() would mix
            // "Today" with the OS locale's weekday on non-English systems.
            string dayName = DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate.ToString("dddd", CultureInfo.InvariantCulture)
                : $"Day {i + 1}";
            items.Add(new DailyForecastItem(
                i == 0 ? "Today" : dayName,
                maxes[i].GetDouble(),
                mins[i].GetDouble(),
                codes[i].GetInt32()));
        }
        return items;
    }

    /// <summary>The resolution identity key — one spelling for the client's
    /// per-query geocode cache and the widget's in-flight staleness guard:
    /// a change in any resolution input (Location, Latitude, Longitude,
    /// CountryCode, LocationMatch) yields a different key. Fields are
    /// backslash-escaped and joined with '|' so a separator character inside
    /// a field can never forge a colliding key.</summary>
    internal static string BuildQueryKey(WeatherLocation location)
        => string.Join('|',
            EscapeKeyField(location.LocationType), EscapeKeyField(location.Location),
            EscapeKeyField(location.Latitude), EscapeKeyField(location.Longitude),
            EscapeKeyField(location.CountryCode), EscapeKeyField(location.LocationMatch));

    private static string EscapeKeyField(string? value)
        => (value ?? "").Replace("\\", "\\\\").Replace("|", "\\|");

    /// <summary>
    /// The inspector's search-as-you-type surface: geocodes <paramref name="query"/>
    /// (a city name or a postal code) into ranked candidates with their exact
    /// coordinates and population. Returns an empty list on any failure — never
    /// throws; cancellation propagates so the editor can discard stale responses.
    /// The fetch + parse + candidate shaping live in <see cref="WeatherGeocoder"/>.
    /// </summary>
    public Task<IReadOnlyList<GeocodeCandidate>> SearchCitiesAsync(string query, CancellationToken cancellationToken = default)
        => _geocoder.SearchCitiesAsync(query, cancellationToken);

    private sealed class WeatherCacheData
    {
        public double CurrentTempC { get; set; }
        public double FeelsLikeC { get; set; }
        public double Humidity { get; set; }
        public double WindSpeedKmH { get; set; }
        public int WeatherCode { get; set; }
        public double HighTempC { get; set; }
        public double LowTempC { get; set; }
        public string? ResolvedCityName { get; set; }
        public double? Lat { get; set; }
        public double? Lon { get; set; }

        /// <summary>The resolution query key this cache was saved for
        /// (<see cref="BuildQueryKey"/>); null/empty on legacy caches that
        /// predate the identity check.</summary>
        public string? LocationQueryKey { get; set; }
        public List<DailyForecastData> DailyForecasts { get; set; } = [];
        public List<HourlyForecastData> HourlyForecasts { get; set; } = [];
    }

    private sealed class DailyForecastData
    {
        public string DayName { get; set; } = "";
        public double MaxTempC { get; set; }
        public double MinTempC { get; set; }
        public int WeatherCode { get; set; }
    }

    private sealed class HourlyForecastData
    {
        public string TimeLabel { get; set; } = "";
        public double TempC { get; set; }
        public int WeatherCode { get; set; }
    }
}
