using System.Globalization;
using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>
/// A day-row of the daily forecast strip. Shared data model between
/// <see cref="WeatherClient"/> (producer) and the rendering widgets (consumer).
/// </summary>
public readonly record struct DailyForecastItem(string DayName, double MaxTempC, double MinTempC, int WeatherCode);

/// <summary>
/// One column of the hourly forecast strip. Shared data model between
/// <see cref="WeatherClient"/> (producer) and the rendering widgets (consumer).
/// </summary>
public readonly record struct HourlyForecastItem(string TimeLabel, double TempC, int WeatherCode);

/// <summary>
/// A resolved place the weather is fetched for. Latitude/Longitude are the
/// optional explicit coordinate overrides; when either is empty the location
/// query (city name, ZIP, or "lat,lon" pair) is resolved via geocoding.
/// <see cref="CountryCode"/> is the optional ISO country-code hint ("US",
/// "DE", ...) that disambiguates same-named cities across countries.
/// </summary>
public sealed record WeatherLocation(string LocationType, string Location, string? Latitude, string? Longitude, string? CustomLabel)
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
public sealed record WeatherSnapshot(
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
/// Deep weather data module: geocode → fetch → parse → disk cache, with an
/// internal 5-minute fetch throttle. The widget layer only renders snapshots
/// returned by <see cref="FetchCurrentAsync"/>.
/// </summary>
public sealed class WeatherClient
{
    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        EnableMultipleHttp2Connections = true
    });

    /// <summary>The fetch throttle window — the one spelling shared by the
    /// atomic claim (<see cref="TryBeginFetch"/>) and the render-tick pre-check
    /// (<see cref="IsFetchWindowElapsed"/>); a change edits one constant.</summary>
    private static readonly TimeSpan FetchWindow = TimeSpan.FromMinutes(5);

    private readonly string _cachePath;
    private readonly Action<string, Exception?>? _logError;

    private DateTime _lastFetchTime = DateTime.MinValue;
    private int _fetchClaim; // 1 = a fetch is in flight (see TryBeginFetch)
    private string _lastLocationQuery = "";

    private double? _lat;
    private double? _lon;
    // Neutral until a resolution sets a real identity (never a hardcoded city).
    private string _resolvedCityName = "Unknown location";

    /// <summary>Test seam: injectable clock for fetch throttling and cache timestamps.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Test seam: substitute HTTP transport for fetch tests (defaults to <see cref="SharedHttpClient"/>).</summary>
    internal HttpClient? TestHttpClient { get; set; }

    private HttpClient Http => TestHttpClient ?? SharedHttpClient;

    /// <summary>The last resolved display name for the location (API name, override label, or fallback).</summary>
    internal string ResolvedCityName => _resolvedCityName;

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

    /// <summary>True when the last geocode resolution ended in a population-decided
    /// tie with no <see cref="WeatherLocation.LocationMatch"/> pick — the widget
    /// must not display weather for an ambiguous name.</summary>
    internal bool LastResolutionAmbiguous { get; private set; }

    /// <summary>UTC timestamp of the last successful fetch or cache load (drives throttling).</summary>
    internal DateTime LastFetchTimeUtc => _lastFetchTime;

    /// <param name="cacheDirectory">Directory for the disk cache (created on demand).</param>
    /// <param name="cacheFileName">Per-instance cache file name; defaults to a shared "weather_default.json".</param>
    /// <param name="timeProvider">Test seam: clock for throttling and cache timestamps.</param>
    /// <param name="http">Test seam: substitute HTTP transport (defaults to the shared client).</param>
    /// <param name="logError">Optional error sink; when omitted, failures are silent.</param>
    public WeatherClient(string cacheDirectory, string? cacheFileName = null, TimeProvider? timeProvider = null, HttpClient? http = null, Action<string, Exception?>? logError = null)
    {
        _cachePath = Path.Combine(cacheDirectory, cacheFileName ?? "weather_default.json");
        Clock = timeProvider ?? TimeProvider.System;
        TestHttpClient = http;
        _logError = logError;
        Directory.CreateDirectory(cacheDirectory);
    }

    /// <summary>
    /// Atomically claims a fetch slot: true when a fetch may start (not
    /// already in flight and, unless forced, not throttled). The in-flight
    /// guard is Interlocked — the render tick, the refresh timer, and OnTouch
    /// can race, and a check-then-set would let two of them through.
    /// </summary>
    internal bool TryBeginFetch(bool force = false)
    {
        if (Interlocked.CompareExchange(ref _fetchClaim, 1, 0) != 0) return false;
        if (!force && (Clock.GetUtcNow().UtcDateTime - _lastFetchTime) < FetchWindow)
        {
            Interlocked.Exchange(ref _fetchClaim, 0);
            return false;
        }
        return true;
    }

    private void EndFetch() => Interlocked.Exchange(ref _fetchClaim, 0);

    /// <summary>
    /// Sync throttle pre-check for the render tick: true when no coordinates
    /// are resolved yet or the 5-minute fetch window has elapsed. The render
    /// kick gates on this to avoid the per-frame async allocation of
    /// <see cref="TryBeginFetch"/>; the atomic claim remains the authority.
    /// </summary>
    /// <summary>Synchronous render-tick pre-check: has the throttle window
    /// elapsed? The first attempt (never-fetched) reads as elapsed; a failed
    /// attempt stamps the time, so failures cool down like successes. The
    /// window is the single <see cref="FetchWindow"/> both this check and the
    /// atomic claim share — one spelling, drift impossible.</summary>
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
        _lat = null;
        _lon = null;
        _lastFetchTime = DateTime.MinValue;
        _lastLocationQuery = "";
        LastCandidates = [];
    }

    /// <summary>
    /// Resets only the resolved coordinates and throttle — the geocode
    /// candidates stay. Used when the Location Match pick itself changes, so
    /// the pick can resolve against the candidates it was offered from.
    /// </summary>
    internal void InvalidateCoordinates()
    {
        _lat = null;
        _lon = null;
        _lastFetchTime = DateTime.MinValue;
        _lastLocationQuery = "";
    }

    /// <summary>
    /// Resolves the location (geocode or explicit coordinates), fetches current
    /// + hourly + daily weather from Open-Meteo in one request, parses it into a
    /// snapshot, and writes the disk cache. Returns null when throttled, already
    /// in flight, or on failure — consumers keep their previous values.
    /// </summary>
    public async Task<WeatherSnapshot?> FetchCurrentAsync(WeatherLocation location, bool force = false, CancellationToken cancellationToken = default)
    {
        if (!TryBeginFetch(force)) return null;

        try
        {
            // The ambiguity gate's outcome describes this resolution: clear any
            // stale flag before resolving so a successful fetch can never carry
            // an old ambiguity forward (the coordinate paths leave it false).
            LastResolutionAmbiguous = false;

            string currentQuery = $"{location.LocationType}_{location.Location}_{location.Latitude}_{location.Longitude}_{location.CountryCode}_{location.LocationMatch}";
            if (!_lat.HasValue || _lastLocationQuery != currentQuery || force)
                await ResolveCoordinatesAsync(location, currentQuery).ConfigureAwait(false);

            if (!_lat.HasValue || !_lon.HasValue) return null;

            double lat = _lat.Value;
            double lon = _lon.Value;

            string forecastUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}&current=temperature_2m,relative_humidity_2m,apparent_temperature,weather_code,wind_speed_10m,wind_direction_10m,is_day,precipitation,cloud_cover&hourly=temperature_2m,relative_humidity_2m,weather_code&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto";
            string json = await Http.GetStringAsync(forecastUrl, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var (tempC, feelsLikeC, windSpeedKmH, weatherCode) = ParseCurrentWeather(root);
            var (humidity, hourlyForecasts) = ParseHourlyForecast(root);
            var (highTempC, lowTempC, dailyForecasts) = ParseDailyForecast(root);
            var snapshot = new WeatherSnapshot(
                tempC, feelsLikeC, humidity, windSpeedKmH, weatherCode, highTempC, lowTempC,
                dailyForecasts, hourlyForecasts, _resolvedCityName, lat, lon);

            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            await SaveCacheAsync(snapshot).ConfigureAwait(false);
            return snapshot;
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Weather fetch failed: {ex.Message}", ex);
            // Stamp the attempt time so a failure cools down like a success —
            // otherwise the widget's render tick sees an elapsed window and
            // retries at frame rate during an outage (request + log storm).
            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            return null;
        }
        finally
        {
            EndFetch();
            FetchCompletedCount++;
        }
    }

    /// <summary>
    /// Loads the disk cache and returns the stored snapshot (if any). On success
    /// the fetch throttle is primed to "now" so a freshly cached widget does not
    /// immediately re-fetch — matching the widget's boot semantics.
    /// </summary>
    public async Task<WeatherSnapshot?> LoadCacheAsync()
    {
        try
        {
            string path = _cachePath;
            if (!File.Exists(path)) return null;
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<WeatherCacheData>(json);
            if (data == null) return null;
            // A cache without a resolved name must not invent one (the old
            // "New York" fallback mislabeled any location) — use the cached
            // coordinates, the only truthful identity the cache carries.
            if (!string.IsNullOrWhiteSpace(data.ResolvedCityName))
            {
                _resolvedCityName = data.ResolvedCityName;
            }
            else if (data.Lat is double cachedLat && data.Lon is double cachedLon)
            {
                _resolvedCityName = $"{cachedLat.ToString("F2", CultureInfo.InvariantCulture)}, {cachedLon.ToString("F2", CultureInfo.InvariantCulture)}";
            }
            else
            {
                _resolvedCityName = "Unknown location";
            }
            _lat = data.Lat;
            _lon = data.Lon;
            _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
            return new WeatherSnapshot(
                data.CurrentTempC,
                data.FeelsLikeC,
                data.Humidity,
                data.WindSpeedKmH,
                data.WeatherCode,
                data.HighTempC,
                data.LowTempC,
                data.DailyForecasts.Select(d => new DailyForecastItem(d.DayName, d.MaxTempC, d.MinTempC, d.WeatherCode)).ToArray(),
                data.HourlyForecasts.Select(h => new HourlyForecastItem(h.TimeLabel, h.TempC, h.WeatherCode)).ToArray(),
                _resolvedCityName,
                data.Lat ?? 0,
                data.Lon ?? 0);
        }
        catch (Exception ex)
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
            if (File.Exists(_cachePath)) File.Delete(_cachePath);
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Weather cache clear failed: {ex.Message}", ex);
        }
    }

    private async Task SaveCacheAsync(WeatherSnapshot snapshot)
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
                DailyForecasts = (snapshot.DailyForecasts ?? []).Select(d => new DailyForecastData { DayName = d.DayName, MaxTempC = d.MaxTempC, MinTempC = d.MinTempC, WeatherCode = d.WeatherCode }).ToList(),
                HourlyForecasts = (snapshot.HourlyForecasts ?? []).Select(h => new HourlyForecastData { TimeLabel = h.TimeLabel, TempC = h.TempC, WeatherCode = h.WeatherCode }).ToList()
            };
            string json = JsonSerializer.Serialize(data);
            await File.WriteAllTextAsync(_cachePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Weather cache save failed: {ex.Message}", ex);
        }
    }

    private async Task ResolveCoordinatesAsync(WeatherLocation location, string currentQuery)
    {
        _lastLocationQuery = currentQuery;

        // Explicit coordinates are authoritative — they must win over a stale
        // Location Match pick from a previous city query.
        if (double.TryParse(location.Latitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var latVal)
            && double.TryParse(location.Longitude, NumberStyles.Float, CultureInfo.InvariantCulture, out var lonVal))
        {
            _lat = latVal;
            _lon = lonVal;
            _resolvedCityName = string.IsNullOrWhiteSpace(location.CustomLabel)
                ? $"{latVal.ToString("F2", CultureInfo.InvariantCulture)}, {lonVal.ToString("F2", CultureInfo.InvariantCulture)}"
                : location.CustomLabel;
        }
        else if (IsCoordinatePair(location.Location))
        {
            string[] parts = location.Location.Split(',');
            _lat = double.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
            _lon = double.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
            _resolvedCityName = $"{_lat.Value.ToString("F2", CultureInfo.InvariantCulture)}, {_lon.Value.ToString("F2", CultureInfo.InvariantCulture)}";
        }
        else if (IsZipCode(location.Location))
        {
            await GeocodeZipCodeAsync(location).ConfigureAwait(false);
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
                    return;
                }
            }

            await GeocodeCityLocationAsync(location).ConfigureAwait(false);
        }
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
        for (int i = 0; i < Math.Min(hLen, 12); i++)
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
        for (int i = 0; i < Math.Min(dLen, 7); i++)
        {
            string dateStr = dTimes[i].GetString() ?? "";
            string dayName = DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate.DayOfWeek.ToString()
                : $"Day {i + 1}";
            items.Add(new DailyForecastItem(
                i == 0 ? "Today" : dayName,
                maxes[i].GetDouble(),
                mins[i].GetDouble(),
                codes[i].GetInt32()));
        }
        return items;
    }

    /// <summary>The geocoder search URL — the single URL builder shared by the
    /// resolution flow and the inspector's search-as-you-type (cities and postal
    /// codes both resolve as a name query).</summary>
    private static Uri BuildGeocodeSearchUri(string query, string? countryCode)
    {
        string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=10&language=en&format=json";
        if (!string.IsNullOrWhiteSpace(countryCode))
            url += $"&countryCode={Uri.EscapeDataString(countryCode.Trim())}";
        return new Uri(url);
    }

    /// <summary>
    /// The inspector's search-as-you-type surface: geocodes <paramref name="query"/>
    /// (a city name or a postal code) into ranked candidates with their exact
    /// coordinates and population. Returns an empty list on any failure — never
    /// throws; cancellation propagates so the editor can discard stale responses.
    /// </summary>
    public async Task<IReadOnlyList<GeocodeCandidate>> SearchCitiesAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            string json = await Http.GetStringAsync(BuildGeocodeSearchUri(query, null), cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results))
            {
                return [];
            }

            var candidates = new List<GeocodeCandidate>(results.GetArrayLength());
            foreach (var candidate in results.EnumerateArray())
            {
                string label = ComposeResolvedName(candidate, query);
                double population = candidate.TryGetProperty("population", out var p) && p.ValueKind == JsonValueKind.Number
                    ? p.GetDouble()
                    : 0;
                candidates.Add(new GeocodeCandidate(
                    label, label,
                    candidate.GetProperty("latitude").GetDouble(),
                    candidate.GetProperty("longitude").GetDouble())
                {
                    Population = population,
                });
            }
            return candidates;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"Location search failed for '{SanitizeLog(query)}': {ex.Message}", ex);
            return [];
        }
    }

    /// <summary>
    /// Resolves a city-name query via the Open-Meteo geocoding API. The API
    /// ranks by population, so a bare name can resolve to the wrong same-named
    /// city worldwide ("Victoria" -> Vitoria, Brazil). This fetches ten
    /// candidates and ranks them: exact name match first, then a comma-suffix
    /// match ("Springfield, MA" / "Victoria, BC" / "San Jose, Costa Rica")
    /// against admin1/country/country_code, then the <see cref="WeatherLocation.CountryCode"/>
    /// hint. A tie at the top score is deliberately left unresolved — the
    /// ambiguity gate blocks the fetch until the user picks from the
    /// candidates; population never decides the winner (an untrusted
    /// population-decided tie would show wrong-city weather). The resolved
    /// name carries "Name, Admin1, Country" so the widget title shows what
    /// was picked.
    /// </summary>
    private async Task GeocodeCityLocationAsync(WeatherLocation location)
    {
        try
        {
            string query = location.Location.Trim();
            string namePart = query;
            string? suffixPart = null;
            int comma = query.IndexOf(',');
            if (comma > 0)
            {
                namePart = query[..comma].Trim();
                suffixPart = query[(comma + 1)..].Trim();
                if (suffixPart.Length == 0) suffixPart = null;
            }

            string url = BuildGeocodeSearchUri(namePart, location.CountryCode).ToString();

            string json = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                // Every candidate becomes a pickable option ("Location Match"
                // dropdown): label = "Name, Admin1, Country", query = the label
                // text so a pick re-resolves deterministically to this place.
                var candidates = new List<GeocodeCandidate>(results.GetArrayLength());
                foreach (var candidate in results.EnumerateArray())
                {
                    string label = ComposeResolvedName(candidate, namePart);
                    candidates.Add(new GeocodeCandidate(
                        label, label,
                        candidate.GetProperty("latitude").GetDouble(),
                        candidate.GetProperty("longitude").GetDouble()));
                }
                LastCandidates = candidates;

                // A persisted Location Match pick must survive restart/import:
                // candidates are in-memory per instance, so after re-creation
                // the stored pick cannot resolve from cache. If the pick
                // matches a freshly geocoded candidate, promote that candidate
                // to the winner instead of silently reverting to the ranking.
                // The pick resolves a tie deterministically — it runs before
                // the ambiguity gate, so a picked place never reads ambiguous.
                if (!string.IsNullOrWhiteSpace(location.LocationMatch))
                {
                    var picked = candidates.FirstOrDefault(c =>
                        c.Query.Equals(location.LocationMatch.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (picked is not null)
                    {
                        _lat = picked.Lat;
                        _lon = picked.Lon;
                        _resolvedCityName = picked.Label;
                        return;
                    }
                }

                // Rank: collect (score, population, candidate) and detect a
                // population-decided tie — when more than one candidate shares
                // the top score, the winner is untrustworthy without a pick
                // (the "Berlin" problem). The widget must not display
                // wrong-city weather, so coordinates stay unresolved and the
                // fetch returns null (the existing no-coordinates path in
                // FetchCurrentAsync). A single top scorer is the unambiguous
                // winner — population no longer decides anything.
                var ranked = results.EnumerateArray()
                    .Select(c => (Candidate: c, Rank: RankGeocodeCandidate(c, namePart, suffixPart, location.CountryCode)))
                    .ToList();
                int bestScore = ranked.Max(r => r.Rank);
                var topTied = ranked.Where(r => r.Rank == bestScore).ToList();
                if (topTied.Count > 1)
                {
                    LastResolutionAmbiguous = true;
                    _lat = null;
                    _lon = null;
                    // Drop the stale resolved name too: a previous resolution's
                    // name must never trap the next editor with a place the
                    // fetch never reached.
                    _resolvedCityName = "";
                }
                else
                {
                    JsonElement best = topTied[0].Candidate;
                    _lat = best.GetProperty("latitude").GetDouble();
                    _lon = best.GetProperty("longitude").GetDouble();
                    _resolvedCityName = ComposeResolvedName(best, namePart);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Geocoding failed for '{SanitizeLog(location.Location)}': {ex.Message}", ex);
        }

        // A failed or ambiguous geocode leaves the coordinates unresolved:
        // FetchCurrentAsync returns null and the widget renders its "no data"
        // state instead of silently pinning a default location. Stamp the
        // attempt time so the 5-minute throttle applies even without
        // coordinates — otherwise a typo'd city, an ambiguous tie, or an
        // outage would retry at render rate forever.
        _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// Pure geocode-candidate ranking: exact name match dominates; the
    /// comma-suffix (state/country) and the country-code hint add weighted
    /// matches. Returns the score only — the caller deliberately leaves a tie
    /// at the top score unresolved (the ambiguity gate), so population never
    /// decides the winner.
    /// </summary>
    private static int RankGeocodeCandidate(JsonElement candidate, string namePart, string? suffixPart, string? countryCode)
    {
        string name = GetString(candidate, "name");
        string admin1 = GetString(candidate, "admin1");
        string country = GetString(candidate, "country");
        string code = GetString(candidate, "country_code");

        return ScoreExactName(name, namePart)
            + ScoreSuffixMatch(admin1, country, code, suffixPart)
            + ScoreCountryHint(code, country, countryCode);
    }

    private static string GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static int ScoreExactName(string name, string namePart)
        => name.Equals(namePart, StringComparison.OrdinalIgnoreCase) ? 1000 : 0;

    private static int ScoreSuffixMatch(string admin1, string country, string code, string? suffixPart)
    {
        if (string.IsNullOrWhiteSpace(suffixPart)) return 0;

        // A full label suffix ("New Hampshire, United States" — what a pick
        // persists) must match component by component: every component must hit
        // admin1/country/code, else the place does not match the label at all
        // (the population tiebreak must never re-pick a wrong city from a
        // persisted label).
        string[] components = suffixPart.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        int score = 0;
        foreach (string component in components)
        {
            if (EqualsAny(admin1, country, code, component)) score += 500;
            else if (StartsWithAny(admin1, country, code, component)) score += 250;
            else return 0;
        }
        return score;
    }

    private static bool EqualsAny(string admin1, string country, string code, string component)
        => admin1.Equals(component, StringComparison.OrdinalIgnoreCase)
            || country.Equals(component, StringComparison.OrdinalIgnoreCase)
            || code.Equals(component, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithAny(string admin1, string country, string code, string component)
        => admin1.StartsWith(component, StringComparison.OrdinalIgnoreCase)
            || country.StartsWith(component, StringComparison.OrdinalIgnoreCase)
            || code.StartsWith(component, StringComparison.OrdinalIgnoreCase);

    private static int ScoreCountryHint(string code, string country, string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return 0;
        string hint = countryCode.Trim();
        return code.Equals(hint, StringComparison.OrdinalIgnoreCase)
            || country.Equals(hint, StringComparison.OrdinalIgnoreCase)
            ? 500
            : 0;
    }

    /// <summary>"Name, Admin1, Country" (omitting missing parts) so the widget
    /// title shows exactly which place was picked.</summary>
    private static string ComposeResolvedName(JsonElement candidate, string fallbackName)
    {
        string name = candidate.TryGetProperty("name", out var n) ? n.GetString() ?? fallbackName : fallbackName;
        string admin1 = candidate.TryGetProperty("admin1", out var a1) ? a1.GetString() ?? "" : "";
        string country = candidate.TryGetProperty("country", out var c) ? c.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(admin1)) return string.IsNullOrWhiteSpace(country) ? name : $"{name}, {country}";
        return string.IsNullOrWhiteSpace(country) ? $"{name}, {admin1}" : $"{name}, {admin1}, {country}";
    }

    private async Task GeocodeZipCodeAsync(WeatherLocation location)
    {
        string zipCode = location.Location.Trim();
        try
        {
            string url = $"https://api.zippopotam.us/us/{Uri.EscapeDataString(zipCode)}";
            string json = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            _lat = root.GetProperty("latitude").GetDouble();
            _lon = root.GetProperty("longitude").GetDouble();
            string city = root.TryGetProperty("place name", out var place) ? place.GetString() ?? "" : "";
            string state = root.TryGetProperty("state", out var st) ? st.GetString() ?? "" : "";
            _resolvedCityName = string.IsNullOrWhiteSpace(state) ? city : $"{city}, {state}";
            return;
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"ZIP geocoding failed for '{SanitizeLog(zipCode)}': {ex.Message}", ex);
        }

        // The zippopotam path is US-only; fall back to the worldwide Open-Meteo
        // geocoder WITH the original location (so the CountryCode hint is
        // carried — e.g. "10115" + "DE" resolves the Berlin postal district).
        await GeocodeCityLocationAsync(location).ConfigureAwait(false);
    }

    private static bool IsZipCode(string query)
    {
        string trimmed = query.Trim();
        return trimmed.Length == 5 && trimmed.All(char.IsDigit);
    }

    /// <summary>
    /// Flattens user-provided strings before interpolation into log lines so
    /// embedded newlines cannot inject fake log entries.
    /// </summary>
    private static string SanitizeLog(string value)
        => value.Replace('\r', ' ').Replace('\n', ' ');

    private static bool IsCoordinatePair(string query)
    {
        string[] parts = query.Split(',');
        if (parts.Length != 2) return false;
        return double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            && double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
    }

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
