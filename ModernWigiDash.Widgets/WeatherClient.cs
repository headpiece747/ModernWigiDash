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
/// </summary>
public sealed record WeatherLocation(string LocationType, string Location, string? Latitude, string? Longitude, string? CustomLabel);

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

    private readonly string _cachePath;
    private readonly Action<string, Exception?>? _logError;

    private DateTime _lastFetchTime = DateTime.MinValue;
    private int _fetchClaim; // 1 = a fetch is in flight (see TryBeginFetch)
    private string _lastLocationQuery = "";

    private double? _lat;
    private double? _lon;
    private string _resolvedCityName = "New York";

    /// <summary>Test seam: injectable clock for fetch throttling and cache timestamps.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Test seam: substitute HTTP transport for fetch tests (defaults to <see cref="SharedHttpClient"/>).</summary>
    internal HttpClient? TestHttpClient { get; set; }

    private HttpClient Http => TestHttpClient ?? SharedHttpClient;

    /// <summary>The last resolved display name for the location (API name, override label, or fallback).</summary>
    internal string ResolvedCityName => _resolvedCityName;

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
        if (!force && (Clock.GetUtcNow().UtcDateTime - _lastFetchTime).TotalMinutes < 5)
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
    /// <summary>Synchronous render-tick pre-check: has the 5-minute throttle
    /// window elapsed? The first attempt (never-fetched) reads as elapsed; a
    /// failed attempt stamps the time, so failures cool down like successes.</summary>
    internal bool IsFetchWindowElapsed()
        => Clock.GetUtcNow().UtcDateTime - _lastFetchTime >= TimeSpan.FromMinutes(5);

    /// <summary>
    /// Resets resolved coordinates and the throttle so the next fetch
    /// re-resolves the location and runs immediately (location property change).
    /// </summary>
    internal void InvalidateLocation()
    {
        _lat = null;
        _lon = null;
        _lastFetchTime = DateTime.MinValue;
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
            string currentQuery = $"{location.LocationType}_{location.Location}_{location.Latitude}_{location.Longitude}";
            if (!_lat.HasValue || _lastLocationQuery != currentQuery || force)
                await ResolveCoordinatesAsync(location, currentQuery).ConfigureAwait(false);

            if (!_lat.HasValue || !_lon.HasValue) return null;

            double lat = _lat.Value;
            double lon = _lon.Value;

            string forecastUrl = $"https://api.open-meteo.com/v1/forecast?latitude={lat:F4}&longitude={lon:F4}&current_weather=true&hourly=temperature_2m,relativehumidity_2m,weathercode&daily=weathercode,temperature_2m_max,temperature_2m_min&apparent_temperature=true&timezone=auto";
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
            return null;
        }
        finally
        {
            EndFetch();
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
            _resolvedCityName = data.ResolvedCityName ?? "New York";
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
            await GeocodeZipCodeAsync(location.Location.Trim()).ConfigureAwait(false);
        }
        else
        {
            await GeocodeCityLocationAsync(location.Location).ConfigureAwait(false);
        }
    }

    private static (double? TempC, double? FeelsLikeC, double? WindSpeedKmH, int? WeatherCode) ParseCurrentWeather(JsonElement root)
    {
        double? tempC = null;
        double? feelsLikeC = null;
        double? windSpeedKmH = null;
        int? weatherCode = null;

        if (!root.TryGetProperty("current_weather", out var currentWeather)) return (null, null, null, null);

        if (currentWeather.TryGetProperty("temperature", out var tempEl))
            tempC = tempEl.GetDouble();
        // The URL requests apparent_temperature=true, which adds
        // apparent_temperature to the legacy current_weather block — the real
        // "feels like" metric (the old code read hourly.temperature_2m, i.e.
        // the plain temperature, silently).
        if (currentWeather.TryGetProperty("apparent_temperature", out var feelsEl))
            feelsLikeC = feelsEl.GetDouble();
        if (currentWeather.TryGetProperty("windspeed", out var windEl))
            windSpeedKmH = windEl.GetDouble();
        if (currentWeather.TryGetProperty("weathercode", out var codeEl))
            weatherCode = codeEl.GetInt32();

        return (tempC, feelsLikeC, windSpeedKmH, weatherCode);
    }

    private static (double? Humidity, IReadOnlyList<HourlyForecastItem>? Hourly) ParseHourlyForecast(JsonElement root)
    {
        if (!root.TryGetProperty("hourly", out var hourly)
            || !hourly.TryGetProperty("temperature_2m", out var temps)
            || temps.GetArrayLength() <= 0) return (null, null);

        double? humidity = null;
        if (hourly.TryGetProperty("relativehumidity_2m", out var hums) && hums.GetArrayLength() > 0)
            humidity = hums[0].GetDouble();

        IReadOnlyList<HourlyForecastItem>? hourlyForecasts = null;
        if (hourly.TryGetProperty("time", out var times) && hourly.TryGetProperty("weathercode", out var codes) && hourly.TryGetProperty("temperature_2m", out var tempsInner))
        {
            int hLen = Math.Min(times.GetArrayLength(), tempsInner.GetArrayLength());
            List<HourlyForecastItem> items = [];
            for (int i = 0; i < Math.Min(hLen, 12); i++)
            {
                string timeStr = times[i].GetString() ?? "";
                string label = timeStr.Length >= 16 ? timeStr[11..16] : $"{i}:00";
                items.Add(new HourlyForecastItem(label, tempsInner[i].GetDouble(), codes[i].GetInt32()));
            }
            hourlyForecasts = items;
        }
        return (humidity, hourlyForecasts);
    }

    private static (double? HighTempC, double? LowTempC, IReadOnlyList<DailyForecastItem>? Daily) ParseDailyForecast(JsonElement root)
    {
        double? highTempC = null;
        double? lowTempC = null;
        IReadOnlyList<DailyForecastItem>? dailyForecasts = null;

        if (!root.TryGetProperty("daily", out var daily)) return (null, null, null);

        if (daily.TryGetProperty("temperature_2m_max", out var maxes) && maxes.GetArrayLength() > 0)
            highTempC = maxes[0].GetDouble();
        if (daily.TryGetProperty("temperature_2m_min", out var mins) && mins.GetArrayLength() > 0)
            lowTempC = mins[0].GetDouble();

        if (daily.TryGetProperty("time", out var dTimes) && daily.TryGetProperty("weathercode", out var dCodes) && daily.TryGetProperty("temperature_2m_max", out _))
        {
            int dLen = Math.Min(dTimes.GetArrayLength(), maxes.GetArrayLength());
            List<DailyForecastItem> items = [];
            for (int i = 0; i < Math.Min(dLen, 7); i++)
            {
                string dateStr = dTimes[i].GetString() ?? "";
                string dayName = DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate) ? parsedDate.DayOfWeek.ToString() : $"Day {i + 1}";
                items.Add(new DailyForecastItem(
                    i == 0 ? "Today" : dayName,
                    maxes[i].GetDouble(),
                    mins[i].GetDouble(),
                    dCodes[i].GetInt32()));
            }
            dailyForecasts = items;
        }
        return (highTempC, lowTempC, dailyForecasts);
    }

    private async Task GeocodeCityLocationAsync(string query)
    {
        try
        {
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(query)}&count=1&language=en&format=json";
            string json = await Http.GetStringAsync(url).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results) && results.GetArrayLength() > 0)
            {
                var first = results[0];
                _lat = first.GetProperty("latitude").GetDouble();
                _lon = first.GetProperty("longitude").GetDouble();
                _resolvedCityName = first.TryGetProperty("name", out var n) ? n.GetString() ?? query : query;
                return;
            }
        }
        catch (Exception ex)
        {
            _logError?.Invoke($"Geocoding failed for '{SanitizeLog(query)}': {ex.Message}", ex);
        }

        // A failed geocode leaves the coordinates unresolved: FetchCurrentAsync
        // returns null and the widget renders its "no data" state instead of
        // silently pinning a default location. Stamp the attempt time so the
        // 5-minute throttle applies even without coordinates — otherwise a
        // typo'd city or an outage would retry at render rate forever.
        _lastFetchTime = Clock.GetUtcNow().UtcDateTime;
    }

    private async Task GeocodeZipCodeAsync(string zipCode)
    {
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

        await GeocodeCityLocationAsync(zipCode).ConfigureAwait(false);
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
