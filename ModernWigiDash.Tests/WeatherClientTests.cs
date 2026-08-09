using System.IO;
using System.Net;
using System.Net.Http;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class WeatherClientTests
{
    private const string SampleForecast = """
    {
      "latitude": 40.7128, "longitude": -74.006,
      "current_weather": { "temperature": 12.5, "windspeed": 8.2, "weathercode": 2, "time": "2026-08-07T12:00" },
      "hourly": {
        "time": ["2026-08-07T12:00", "2026-08-07T13:00"],
        "temperature_2m": [12.5, 13.1],
        "relativehumidity_2m": [60, 58],
        "weathercode": [2, 2]
      },
      "daily": {
        "time": ["2026-08-07", "2026-08-08"],
        "weathercode": [2, 3],
        "temperature_2m_max": [18.0, 20.0],
        "temperature_2m_min": [9.0, 11.0]
      }
    }
    """;

    private const string SampleGeocode = """
    {
      "results": [ { "name": "Berlin", "latitude": 52.52, "longitude": 13.405, "country": "Germany" } ]
    }
    """;

    // The ZIP parse reads root-level latitude/longitude, so the test body
    // mirrors the shape the parser expects (numeric root values).
    private const string SampleZip = """
    {
      "post code": "10001",
      "latitude": 40.7505,
      "longitude": -73.9962,
      "place name": "New York City",
      "state": "New York"
    }
    """;

    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "wmd-weather-client-tests");

    private static WeatherLocation CoordinateLocation => new("Fixed Location", "40.71,-74.00", null, null, null);

    private static WeatherClient CreateClient(HttpMessageHandler stub, FakeClock? clock = null, string? cacheDirectory = null)
        => new(cacheDirectory ?? NewTempDir(), "weather_test.json", timeProvider: clock, http: new HttpClient(stub));

    private static string NewTempDir() => Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));

    private static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHandler.Ok(SampleGeocode);
        if (url.Contains("zippopotam", StringComparison.Ordinal)) return StubHandler.Ok(SampleZip);
        if (url.Contains("/v1/forecast", StringComparison.Ordinal)) return StubHandler.Ok(SampleForecast);
        return StubHandler.NotFound();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(TempRoot, recursive: true); } catch { /* best-effort */ }
    }

    [TestMethod]
    public async Task FetchCurrentAsync_CoordinatePair_ParsesFullSnapshot()
    {
        var stub = new StubHandler(Respond);
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(CoordinateLocation);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(12.5, snapshot.CurrentTempC);
        Assert.AreEqual(8.2, snapshot.WindSpeedKmH);
        Assert.AreEqual(2, snapshot.WeatherCode);
        Assert.AreEqual(12.5, snapshot.FeelsLikeC);
        Assert.AreEqual(60, snapshot.Humidity);
        Assert.AreEqual(18.0, snapshot.HighTempC);
        Assert.AreEqual(9.0, snapshot.LowTempC);
        Assert.AreEqual(2, snapshot.DailyForecasts!.Count);
        Assert.AreEqual(2, snapshot.HourlyForecasts!.Count);
        Assert.AreEqual("Today", snapshot.DailyForecasts[0].DayName);
        Assert.AreEqual("12:00", snapshot.HourlyForecasts[0].TimeLabel);
        Assert.AreEqual("40.71, -74.00", snapshot.ResolvedCityName);
        Assert.AreEqual(40.71, snapshot.Lat);
        Assert.AreEqual(-74.00, snapshot.Lon);
        Assert.AreEqual(1, stub.Calls, "A coordinate-pair location must skip geocoding entirely");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_CityGeocode_ResolvesViaGeocodingApi()
    {
        var stub = new StubHandler(Respond);
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual("Berlin", snapshot.ResolvedCityName, "The geocoding API's name must be used");
        Assert.AreEqual(52.52, snapshot.Lat);
        Assert.AreEqual(13.405, snapshot.Lon);
        Assert.AreEqual(2, stub.Calls, "Geocode + forecast must be exactly two calls");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_ZipGeocode_ResolvesViaZippopotam()
    {
        var stub = new StubHandler(Respond);
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "10001", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(40.7505, snapshot.Lat);
        Assert.AreEqual(-73.9962, snapshot.Lon);
        Assert.AreEqual("New York City, New York", snapshot.ResolvedCityName);
        Assert.AreEqual(2, stub.Calls, "ZIP geocode + forecast must be exactly two calls");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_GeocodeFailure_FallsBackToDefaultCoordinates()
    {
        var stub = new StubHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("/v1/forecast", StringComparison.Ordinal)
                ? StubHandler.Ok(SampleForecast)
                : StubHandler.NotFound());
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Atlantis", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(40.7128, snapshot.Lat, "A failed geocode must fall back to the New York default");
        Assert.AreEqual(-74.0060, snapshot.Lon);
        Assert.AreEqual("Atlantis", snapshot.ResolvedCityName);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_ExplicitLatLonOverride_SkipsGeocoding()
    {
        var stub = new StubHandler(Respond);
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "New York", "40.1", "-75.2", null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(40.1, snapshot.Lat);
        Assert.AreEqual(-75.2, snapshot.Lon);
        Assert.AreEqual("40.10, -75.20", snapshot.ResolvedCityName);
        Assert.AreEqual(1, stub.Calls, "Explicit coordinates must skip geocoding");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_Throttle_UsesInjectedClock()
    {
        var stub = new StubHandler(Respond);
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var client = CreateClient(stub, clock);

        // Seed the throttle window (result intentionally unused).
        await client.FetchCurrentAsync(CoordinateLocation, force: true);
        int afterFirst = stub.Calls;

        var throttled = await client.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNull(throttled, "Within the 5-minute window a non-forced fetch must be throttled away");
        Assert.AreEqual(afterFirst, stub.Calls, "The 5-minute throttle must suppress a second fetch");

        clock.Advance(TimeSpan.FromMinutes(6));
        var resumed = await client.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNotNull(resumed, "After the throttle window elapses, fetching resumes");
        Assert.IsTrue(stub.Calls > afterFirst, "After the throttle window elapses, the transport must be hit again");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_InFlight_ReturnsNull()
    {
        var gate = new TaskCompletionSource();
        var stub = new BlockingHandler(gate, SampleForecast);
        var client = CreateClient(stub);

        var inFlight = client.FetchCurrentAsync(CoordinateLocation);
        await WaitUntilAsync(() => stub.Calls == 1);

        var second = await client.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNull(second, "A fetch while one is already in flight must be skipped");

        gate.SetResult();
        var first = await inFlight;
        Assert.IsNotNull(first, "The in-flight fetch itself must complete successfully");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_FetchFailure_ReturnsNullAndRecovers()
    {
        bool fail = true;
        var stub = new StubHandler(_ => fail ? StubHandler.NotFound() : StubHandler.Ok(SampleForecast));
        var client = CreateClient(stub);

        var failed = await client.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNull(failed, "A failed fetch must yield null, not throw");

        fail = false;
        var recovered = await client.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNotNull(recovered, "A subsequent fetch must succeed (the in-flight flag must be cleared)");
        Assert.AreEqual(12.5, recovered.CurrentTempC);
    }

    [TestMethod]
    public async Task InvalidateLocation_ResetsThrottle_SoNextFetchRuns()
    {
        var stub = new StubHandler(Respond);
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var client = CreateClient(stub, clock);

        await client.FetchCurrentAsync(CoordinateLocation, force: true);
        Assert.AreEqual(1, stub.Calls);

        client.InvalidateLocation();
        var refreshed = await client.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNotNull(refreshed, "Invalidation must clear the throttle so a non-forced fetch runs");
        Assert.AreEqual(2, stub.Calls);
    }

    [TestMethod]
    public async Task LoadCacheAsync_RoundTrips_FetchedSnapshot()
    {
        string dir = NewTempDir();
        var stub = new StubHandler(Respond);
        var writer = CreateClient(stub, cacheDirectory: dir);

        var fetched = await writer.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNotNull(fetched);

        var reader = new WeatherClient(dir, "weather_test.json", http: new HttpClient(new StubHandler(Respond)));
        var loaded = await reader.LoadCacheAsync();

        Assert.IsNotNull(loaded);
        Assert.AreEqual(12.5, loaded.CurrentTempC);
        Assert.AreEqual(60, loaded.Humidity);
        Assert.AreEqual(2, loaded.DailyForecasts!.Count);
        Assert.AreEqual(2, loaded.HourlyForecasts!.Count);
        Assert.AreEqual(40.71, loaded.Lat);
        Assert.AreEqual("40.71, -74.00", loaded.ResolvedCityName);
        Assert.AreNotEqual(DateTime.MinValue, reader.LastFetchTimeUtc, "A successful cache load must prime the fetch throttle");
    }

    [TestMethod]
    public async Task LoadCacheAsync_NoCacheFile_ReturnsNull()
    {
        var client = new WeatherClient(NewTempDir(), "weather_test.json", http: new HttpClient(new StubHandler(Respond)));

        var loaded = await client.LoadCacheAsync();

        Assert.IsNull(loaded);
    }

    [TestMethod]
    public async Task ClearCache_DeletesCacheFile()
    {
        string dir = NewTempDir();
        var stub = new StubHandler(Respond);
        var writer = CreateClient(stub, cacheDirectory: dir);
        await writer.FetchCurrentAsync(CoordinateLocation);

        var reader = new WeatherClient(dir, "weather_test.json", http: new HttpClient(new StubHandler(Respond)));
        Assert.IsNotNull(await reader.LoadCacheAsync());

        reader.ClearCache();

        Assert.IsNull(await reader.LoadCacheAsync(), "After ClearCache the cache file must be gone");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int Calls { get; private set; }

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_respond(request));
        }

        public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
        public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
    }

    /// <summary>Async handler that parks the request until the gate completes.</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate;
        private readonly string _body;
        public int Calls { get; private set; }

        public BlockingHandler(TaskCompletionSource gate, string body)
        {
            _gate = gate;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            await _gate.Task;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) };
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.IsTrue(condition(), "Condition was not met within the wait budget");
    }
}
