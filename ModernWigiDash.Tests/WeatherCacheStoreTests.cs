using System.IO;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class WeatherCacheStoreTests
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "wmd-weather-cache-store-tests");

    /// <summary>A pre-stamp cache JSON fixture: parses as an unstamped legacy
    /// cache (no LocationQueryKey), exactly like the client-level fixture.</summary>
    private const string LegacyCacheJson = """
        {
          "CurrentTempC": 12.5, "FeelsLikeC": 10.1, "Humidity": 60, "WindSpeedKmH": 8.2,
          "WeatherCode": 2, "HighTempC": 18, "LowTempC": 9, "ResolvedCityName": "Paris",
          "Lat": 48.85, "Lon": 2.35, "DailyForecasts": [], "HourlyForecasts": []
        }
        """;

    private static WeatherSnapshot SnapshotOf(string resolvedName = "Cached", double lat = 48.85, double lon = 2.35)
        => new(12.5, 10.1, 60, 8.2, 2, 18, 9,
            [new DailyForecastItem("Today", 18, 9, 2)],
            [new HourlyForecastItem("00:00", 12.5, 2)],
            resolvedName, lat, lon);

    private static WeatherCacheStore CreateStore(string dir, string? name = null, List<string>? logs = null)
        => new(dir, () => name ?? "cache.json", logs is null ? null : (message, _) => logs.Add(message));

    private static string NewTempDir() => Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));

    [ClassCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(TempRoot, recursive: true); } catch { /* best-effort */ }
    }

    [TestMethod]
    public async Task LoadAsync_MissingFile_ReturnsNullSilently()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        var logs = new List<string>();

        var loaded = await CreateStore(dir, logs: logs).LoadAsync(CancellationToken.None);

        Assert.IsNull(loaded, "a missing cache file must read as no cache");
        Assert.AreEqual(0, logs.Count, "a missing file is the normal no-cache case — no error log");
    }

    [TestMethod]
    public async Task LoadAsync_LegacyUnstamped_ParsesIntoPayload()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "cache.json"), LegacyCacheJson);

        var payload = await CreateStore(dir).LoadAsync(CancellationToken.None);

        Assert.IsNotNull(payload);
        Assert.AreEqual(12.5, payload.CurrentTempC);
        Assert.AreEqual(60, payload.Humidity);
        Assert.AreEqual("Paris", payload.ResolvedCityName);
        Assert.AreEqual(48.85, payload.Lat);
        Assert.AreEqual(2.35, payload.Lon);
        Assert.IsNull(payload.LocationQueryKey, "a legacy cache has no identity stamp");
        Assert.IsNull(payload.IsDay, "a cache saved before the day/night field carries no is_day fact");
    }

    [TestMethod]
    public async Task SaveThenLoad_RoundTripsWithIdentityStamp()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        var store = CreateStore(dir);

        await store.SaveAsync(SnapshotOf(), "Fixed Location|40.71,-74.00|", CancellationToken.None);
        var payload = await store.LoadAsync(CancellationToken.None);

        Assert.IsNotNull(payload);
        Assert.AreEqual("Fixed Location|40.71,-74.00|", payload.LocationQueryKey,
            "the identity stamp the snapshot was saved for must round-trip");
        Assert.AreEqual("Cached", payload.ResolvedCityName);
        Assert.AreEqual(12.5, payload.CurrentTempC);
        Assert.AreEqual(1, payload.DailyForecasts.Count);
        Assert.AreEqual(1, payload.HourlyForecasts.Count);
        Assert.IsNull(payload.IsDay, "the SnapshotOf fixture carries no day/night fact — an absent flag round-trips as unknown");
    }

    [TestMethod]
    public async Task SaveThenLoad_NightIsDay_RoundTrips()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        var store = CreateStore(dir);
        var snapshot = new WeatherSnapshot(
            12.5, 10.1, 60, 8.2, 0, 18, 9,
            [new DailyForecastItem("Today", 18, 9, 0)],
            [new HourlyForecastItem("23:00", 12.5, 0)],
            "Cached", 48.85, 2.35,
            IsDay: false);

        await store.SaveAsync(snapshot, "key", CancellationToken.None);
        var payload = await store.LoadAsync(CancellationToken.None);

        Assert.IsFalse(payload!.IsDay,
            "the day/night fact must round-trip through the disk cache (a saved night renders a moon at boot)");
    }

    [TestMethod]
    public async Task LoadAsync_OversizedFile_IsRejectedBeforeReading()
    {
        // The size-bound guard belongs to the store: a valid cache payload
        // padded past the cap must be rejected without buffering the whole
        // file (without the guard the parse would succeed).
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.json");
        await File.WriteAllTextAsync(path, """{"CurrentTempC":20.0,"ResolvedCityName":"Berlin"}""" + new string(' ', (int)WeatherCacheStore.MaxCacheBytes));
        var logs = new List<string>();

        var loaded = await CreateStore(dir, logs: logs).LoadAsync(CancellationToken.None);

        Assert.IsNull(loaded, "an oversized cache file must not be loaded");
        Assert.IsTrue(logs.Any(l => l.Contains($"exceeds the {WeatherCacheStore.MaxCacheBytes} byte bound", StringComparison.Ordinal)),
            "the rejection must surface through the error sink");
    }

    [TestMethod]
    public async Task LoadAsync_FileExactlyAtBound_Loads()
    {
        // The size-bound boundary: a file exactly at MaxCacheBytes is not
        // oversized — the initial check is strict > and the bounded read
        // reaches the full length, so the post-loop guard holds.
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.json");
        string payload = LegacyCacheJson + new string(' ', (int)WeatherCacheStore.MaxCacheBytes - LegacyCacheJson.Length);
        await File.WriteAllTextAsync(path, payload);

        var loaded = await CreateStore(dir).LoadAsync(CancellationToken.None);

        Assert.IsNotNull(loaded);
        Assert.AreEqual("Paris", loaded.ResolvedCityName, "a cache file exactly at the bound must parse normally");
    }

    [TestMethod]
    public async Task LoadAsync_MalformedJson_ReturnsNullAndLogs()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "cache.json"), "{ not json");
        var logs = new List<string>();

        var loaded = await CreateStore(dir, logs: logs).LoadAsync(CancellationToken.None);

        Assert.IsNull(loaded, "malformed cache JSON must read as no cache");
        Assert.IsTrue(logs.Any(l => l.Contains("Weather cache load failed", StringComparison.Ordinal)),
            "a parse failure must surface through the error sink");
    }

    [TestMethod]
    public async Task LoadAsync_OversizedLists_AreCappedToFetchLimits()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.json");
        string daily = string.Join(",", Enumerable.Range(0, 20)
            .Select(i => $"{{\"DayName\":\"Day{i}\",\"MaxTempC\":20,\"MinTempC\":10,\"WeatherCode\":1}}"));
        string hourly = string.Join(",", Enumerable.Range(0, 20)
            .Select(i => $"{{\"TimeLabel\":\"{i}:00\",\"TempC\":15,\"WeatherCode\":1}}"));
        await File.WriteAllTextAsync(path, $$"""
        {
          "CurrentTempC": 12.5, "FeelsLikeC": 10.1, "Humidity": 60, "WindSpeedKmH": 8.2,
          "WeatherCode": 2, "HighTempC": 18, "LowTempC": 9, "ResolvedCityName": "Cached",
          "Lat": 48.85, "Lon": 2.35,
          "DailyForecasts": [{{daily}}],
          "HourlyForecasts": [{{hourly}}]
        }
        """);

        var payload = await CreateStore(dir).LoadAsync(CancellationToken.None);

        Assert.IsNotNull(payload);
        Assert.AreEqual(7, payload.DailyForecasts.Count, "daily rows must cap at the fetch limit (MaxFetchDays = 7)");
        Assert.AreEqual(12, payload.HourlyForecasts.Count, "hourly rows must cap at the fetch limit (MaxFetchHours = 12)");
    }

    [TestMethod]
    public async Task LoadAsync_FileGrowsMidRead_IsRejected()
    {
        // The stat-then-read gap guard, pinned on the store: a file that
        // grows after the reader opened must not load — the bounded loop
        // stops at MaxCacheBytes and the post-loop total-vs-length check
        // rejects the truncated read. The payload is valid JSON padded to
        // exactly the cap, so a load that slips through the guard would parse
        // and return a payload (the initial length check alone cannot catch a
        // growth that lands after it).
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cache.json");
        string payload = LegacyCacheJson + new string(' ', (int)WeatherCacheStore.MaxCacheBytes - LegacyCacheJson.Length);
        await File.WriteAllTextAsync(path, payload);
        var logs = new List<string>();
        var store = CreateStore(dir, logs: logs);

        using var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        byte[] growth = new byte[4096];
        bool rejected = false;
        for (int attempt = 0; attempt < 50 && !rejected; attempt++)
        {
            writer.SetLength(payload.Length);
            writer.Position = payload.Length;
            using var started = new ManualResetEventSlim(false);
            using var stop = new CancellationTokenSource();
            var grow = Task.Run(() =>
            {
                started.Set();
                while (!stop.IsCancellationRequested)
                {
                    writer.Write(growth);
                    writer.Flush();
                }
            });
            started.Wait();
            var loaded = await store.LoadAsync(CancellationToken.None);
            await stop.CancelAsync();
            await grow;
            rejected = loaded is null;
        }

        Assert.IsTrue(rejected, "the mid-read growth must be rejected (the post-loop guard reads short at the bound)");
        Assert.IsTrue(logs.Any(l => l.Contains($"exceeds the {WeatherCacheStore.MaxCacheBytes} byte bound", StringComparison.Ordinal)),
            "the rejection must surface through the error sink");
    }

    [TestMethod]
    public async Task SaveAsync_AtomicWrite_LeavesNoOrphanTempFiles()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        var store = CreateStore(dir);

        await store.SaveAsync(SnapshotOf(), "key", CancellationToken.None);

        string[] files = Directory.GetFiles(dir);
        Assert.AreEqual(1, files.Length, "the atomic write must leave exactly the cache file");
        Assert.AreEqual(Path.Combine(dir, "cache.json"), files[0]);
    }

    [TestMethod]
    public async Task Clear_DeletesTheCacheFile()
    {
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        var store = CreateStore(dir);
        await store.SaveAsync(SnapshotOf(), "key", CancellationToken.None);
        Assert.IsNotNull(await store.LoadAsync(CancellationToken.None));

        store.Clear();

        Assert.IsNull(await store.LoadAsync(CancellationToken.None), "after Clear the cache file must be gone");
    }

    [TestMethod]
    public void CacheFileName_Resolves_Lazily_FromProvider()
    {
        string name = "first.json";
        var store = new WeatherCacheStore(NewTempDir(), () => name);

        Assert.AreEqual("first.json", store.CacheFileName);
        name = "second.json";
        Assert.AreEqual("second.json", store.CacheFileName,
            "the name must resolve at each read, not once at construction");
    }
}
