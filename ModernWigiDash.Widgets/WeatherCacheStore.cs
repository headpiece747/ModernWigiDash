using System.Text;
using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The weather disk-cache module: owns the cache file's identity-stamped JSON
/// format, the bounded read (a corrupted or foreign file is rejected before a
/// byte is buffered), the atomic write, and the size bound. The client owns
/// the semantics around the file — whether a stamp matches the loading
/// location and the resolution-state apply — so the file format and its
/// safety rules live apart from the fetch/identity logic. The name is
/// resolved lazily at each load/save/clear so a widget whose InstanceId is
/// assigned after construction keys its cache by the final identity.
/// </summary>
internal sealed class WeatherCacheStore
{
    /// <summary>The disk-cache size bound: a cache file larger than this is
    /// rejected before reading (a corrupted or foreign file must never be
    /// buffered into memory).</summary>
    internal const long MaxCacheBytes = 1024 * 1024;

    private readonly string _cacheDirectory;
    private readonly Func<string> _nameProvider;
    private readonly Action<string, Exception?>? _logError;

    /// <param name="cacheDirectory">Directory for the disk cache (created on demand).</param>
    /// <param name="nameProvider">Resolves the cache file name at each load/save/clear.</param>
    /// <param name="logError">Optional error sink; when omitted, failures are silent.</param>
    internal WeatherCacheStore(string cacheDirectory, Func<string> nameProvider, Action<string, Exception?>? logError = null)
    {
        _cacheDirectory = cacheDirectory;
        _nameProvider = nameProvider;
        _logError = logError;
    }

    /// <summary>The cache file name the provider currently resolves (test seam).</summary>
    internal string CacheFileName => _nameProvider();

    /// <summary>The current cache file path, derived from the live name provider.</summary>
    internal string CachePath => Path.Combine(_cacheDirectory, _nameProvider());

    /// <summary>
    /// Reads and deserializes the cache. Returns null when the file is missing
    /// or fails the bounded read/parse — never throws (except cancellation,
    /// which propagates). The payload is identity-stamped (the resolution
    /// query key the cache was saved for); the CALLER decides whether the
    /// stamp matches the loading location, since that comparison lives in the
    /// fetch/identity semantics this module does not own.
    /// </summary>
    internal async Task<WeatherCachePayload?> LoadAsync(CancellationToken cancellationToken)
    {
        string path = CachePath;
        if (!File.Exists(path)) return null;
        try
        {
            string? json = await ReadBoundedAsync(path, cancellationToken).ConfigureAwait(false);
            if (json is null) return null;
            var data = JsonSerializer.Deserialize<WeatherCacheData>(json);
            if (data == null) return null;
            // Deserialized lists are capped at the fetch limits — a
            // hand-edited or foreign cache cannot smuggle more rows than
            // the API ever returns.
            return new WeatherCachePayload(
                data.CurrentTempC, data.FeelsLikeC, data.Humidity, data.WindSpeedKmH, data.WeatherCode,
                data.HighTempC, data.LowTempC, data.ResolvedCityName, data.Lat, data.Lon, data.LocationQueryKey,
                (data.DailyForecasts ?? []).Take(WeatherForecastLimits.MaxFetchDays).Select(d => new DailyForecastItem(d.DayName, d.MaxTempC, d.MinTempC, d.WeatherCode)).ToArray(),
                (data.HourlyForecasts ?? []).Take(WeatherForecastLimits.MaxFetchHours).Select(h => new HourlyForecastItem(h.TimeLabel, h.TempC, h.WeatherCode)).ToArray(),
                data.IsDay);
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
    /// whole. The body is read through the shared <see cref="BoundedRead"/>
    /// core (the same chunking/limit loop the HTTP legs use). Returns null
    /// (logged) when the file exceeds the cap mid-read (the loop stops at the
    /// cap, so a file larger than the cap is detected by the total read
    /// falling short of the file's length).
    /// </summary>
    private async Task<string?> ReadBoundedAsync(string path, CancellationToken cancellationToken)
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
            byte[] body = await BoundedRead.ReadAsync(fs, MaxCacheBytes, cancellationToken).ConfigureAwait(false);
            if (body.Length < fs.Length)
            {
                _logError?.Invoke($"Weather cache load failed: cache file exceeds the {MaxCacheBytes} byte bound", null);
                return null;
            }
            return Encoding.UTF8.GetString(body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logError?.Invoke($"Weather cache load failed: {ex.Message}", ex);
            return null;
        }
    }

    /// <summary>
    /// Serializes and writes the cache with the identity stamp (the resolution
    /// query key the snapshot was fetched for) as an atomic write: the temp
    /// file is written fully, then moved over the target. A crash mid-write
    /// can never leave a truncated cache that the next boot reads as a fresh
    /// snapshot. The temp name is unique per save — a fixed "&lt;name&gt;.tmp" would
    /// interleave two concurrent writers (e.g. two app instances) and let a
    /// torn file win the move.
    /// </summary>
    internal async Task SaveAsync(WeatherSnapshot snapshot, string queryKey, CancellationToken cancellationToken)
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
                // The day/night fact rides the cache (null on a snapshot that
                // never carried is_day — a legacy cache reads as day).
                IsDay = snapshot.IsDay,
                // The identity stamp: the query key this snapshot was fetched
                // for. LoadAsync returns it with the payload; the caller
                // applies the cache only when the stamp matches the loading
                // location's key (or is empty = legacy).
                LocationQueryKey = queryKey,
                DailyForecasts = (snapshot.DailyForecasts ?? []).Select(d => new DailyForecastData { DayName = d.DayName, MaxTempC = d.MaxTempC, MinTempC = d.MinTempC, WeatherCode = d.WeatherCode }).ToList(),
                HourlyForecasts = (snapshot.HourlyForecasts ?? []).Select(h => new HourlyForecastData { TimeLabel = h.TimeLabel, TempC = h.TempC, WeatherCode = h.WeatherCode }).ToList()
            };
            string json = JsonSerializer.Serialize(data);
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

    /// <summary>Deletes the disk cache (internal test seam — production never
    /// clears the cache at runtime).</summary>
    internal void Clear()
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

        /// <summary>The day/night fact the snapshot carried (null on legacy
        /// caches saved before the field existed — they read as unknown, and
        /// the display's default is day).</summary>
        public bool? IsDay { get; set; }

        /// <summary>The resolution query key this cache was saved for
        /// (null/empty on legacy caches that predate the identity check).</summary>
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

/// <summary>
/// The identity-stamped cache read (<see cref="WeatherCacheStore"/>'s output):
/// the stored snapshot fields plus the resolution query key the cache was
/// saved for. A null <see cref="WeatherCachePayload.LocationQueryKey"/> is a
/// legacy cache (predates the identity check) and is trusted by the caller as
/// before.
/// </summary>
internal sealed record WeatherCachePayload(
    double CurrentTempC,
    double FeelsLikeC,
    double Humidity,
    double WindSpeedKmH,
    int WeatherCode,
    double HighTempC,
    double LowTempC,
    string? ResolvedCityName,
    double? Lat,
    double? Lon,
    string? LocationQueryKey,
    IReadOnlyList<DailyForecastItem> DailyForecasts,
    IReadOnlyList<HourlyForecastItem> HourlyForecasts,
    bool? IsDay);
