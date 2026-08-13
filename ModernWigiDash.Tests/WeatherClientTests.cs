using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class WeatherClientTests
{
    private const string SampleForecast = """
    {
      "latitude": 40.7128, "longitude": -74.006,
      "current_weather": { "temperature": 12.5, "apparent_temperature": 10.1, "windspeed": 8.2, "weathercode": 2, "time": "2026-08-07T12:00" },
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

    // Two same-named cities across countries: the exact-name match must beat
    // the higher-population fuzzy match (the Vitoria/Victoria bug).
    private const string SampleSameNameMultiCountry = """
    {
      "results": [
        { "name": "Victoria", "latitude": 48.4284, "longitude": -123.3656, "admin1": "British Columbia", "country": "Canada", "country_code": "CA", "population": 335696 },
        { "name": "Vit\u00f3ria", "latitude": -20.3194, "longitude": -40.3378, "admin1": "Esp\u00edrito Santo", "country": "Brazil", "country_code": "BR", "population": 1962476 }
      ]
    }
    """;

    // Two same-named cities in one country: the state suffix must pick the
    // right admin1 even when the wrong one is listed first with more people.
    private const string SampleSpringfields = """
    {
      "results": [
        { "name": "Springfield", "latitude": 37.21533, "longitude": -93.29824, "admin1": "Missouri", "country": "United States", "country_code": "US", "population": 167601 },
        { "name": "Springfield", "latitude": 42.10148, "longitude": -72.58981, "admin1": "Massachusetts", "country": "United States", "country_code": "US", "population": 155932 }
      ]
    }
    """;

    // Identical names across countries: the CountryCode hint must decide.
    private const string SampleSanJoses = """
    {
      "results": [
        { "name": "San Jose", "latitude": 37.33939, "longitude": -121.89496, "admin1": "California", "country": "United States", "country_code": "US", "population": 1026908 },
        { "name": "San Jose", "latitude": 9.92807, "longitude": -84.09072, "admin1": "San Jos\u00e9 Province", "country": "Costa Rica", "country_code": "CR", "population": 335007 }
      ]
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

    private static WeatherClient CreateClient(HttpMessageHandler stub, FakeTimeProvider? clock = null, string? cacheDirectory = null)
        => new(cacheDirectory ?? NewTempDir(), "weather_test.json", timeProvider: clock, http: new HttpClient(stub));

    private static string NewTempDir() => Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));

    private static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        string url = request.RequestUri?.AbsoluteUri ?? "";
        if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleGeocode);
        if (url.Contains("zippopotam", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleZip);
        if (url.Contains("/v1/forecast", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleForecast);
        return StubHttpHandler.NotFound();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(TempRoot, recursive: true); } catch { /* best-effort */ }
    }

    [TestMethod]
    public async Task FetchCurrentAsync_CoordinatePair_ParsesFullSnapshot()
    {
        var stub = new StubHttpHandler(Respond);
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(CoordinateLocation);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(12.5, snapshot.CurrentTempC);
        Assert.AreEqual(8.2, snapshot.WindSpeedKmH);
        Assert.AreEqual(2, snapshot.WeatherCode);
        Assert.AreEqual(10.1, snapshot.FeelsLikeC, "Feels-like must come from apparent_temperature, not the plain temperature");
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
    public async Task FetchCurrentAsync_LocationMatchPick_ResolvesToExactCandidateCoordinates()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSameNameMultiCountry);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // First resolution populates the candidates (exact match wins by ranking).
        await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null));
        Assert.IsTrue(client.LastCandidates.Count >= 2, "Candidates must be exposed for the Location Match dropdown");

        // A user pick resolves DIRECTLY to that candidate — no re-geocode.
        string picked = client.LastCandidates[^1].Label; // Vitoria, Brazil
        int geocodesBefore = stub.RequestUrls.Count(u => u.Contains("/v1/search", StringComparison.Ordinal));
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null) { LocationMatch = picked }, force: true);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(-20.3194, snapshot.Lat);
        Assert.AreEqual(-40.3378, snapshot.Lon);
        Assert.AreEqual(picked, snapshot.ResolvedCityName);
        Assert.AreEqual(geocodesBefore, stub.RequestUrls.Count(u => u.Contains("/v1/search", StringComparison.Ordinal)),
            "A pick must not re-geocode — it resolves from the cached candidates");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_StaleLocationMatch_FallsBackToGeocode()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSameNameMultiCountry);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // The pick references a candidate that no longer exists (the query
        // changed and re-geocoded, replacing the candidate list) — must fall
        // back to normal geocoding instead of failing.
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null) { LocationMatch = "Gone, Nowhere, Atlantis" });

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(48.4284, snapshot.Lat, "Fallback geocoding must resolve the exact-name match (Victoria, Canada)");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_LocationChangedAfterPick_RegeocodesNewLocation()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/v1/search", StringComparison.Ordinal))
            {
                // Different name => different candidates (Berlin's geocode).
                return StubHttpHandler.Ok(url.Contains("name=Berlin", StringComparison.OrdinalIgnoreCase)
                    ? """{ "results": [ { "name": "Berlin", "latitude": 52.52, "longitude": 13.405, "country": "Germany" } ] }"""
                    : SampleSameNameMultiCountry);
            }
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // Pick Vitoria (the last candidate) from the "Victoria" resolution.
        await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null));
        string picked = client.LastCandidates[^1].Label; // Vitoria, Brazil
        var pickSnapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null) { LocationMatch = picked }, force: true);
        Assert.AreEqual(-20.3194, pickSnapshot!.Lat, "The pick itself must still resolve to Vitoria");

        // Changing Location must drop the candidates, so the stale pick cannot
        // win: "Berlin" must geocode to Berlin.
        client.InvalidateLocation();
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin", null, null, null) { LocationMatch = picked }, force: true);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(52.52, snapshot.Lat, "After InvalidateLocation the stale pick must not override the new Location");
        Assert.AreEqual(13.405, snapshot.Lon);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_ExplicitCoordinates_WinOverStalePick()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSameNameMultiCountry);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // A pick from a previous city resolution must never override explicit
        // lat/lon overrides (they are documented as authoritative).
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", "40.1", "-75.2", null) { LocationMatch = "Victoria, British Columbia, Canada" });

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(40.1, snapshot.Lat);
        Assert.AreEqual(-75.2, snapshot.Lon);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_ZipFallback_CarriesCountryCodeHint()
    {
        string? searchUrl = null;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("zippopotam", StringComparison.Ordinal)) return StubHttpHandler.NotFound(); // US lookup fails for non-US zip
            if (url.Contains("/v1/search", StringComparison.Ordinal))
            {
                searchUrl = url;
                return StubHttpHandler.Ok("""{ "results": [ { "name": "Berlin", "latitude": 52.52, "longitude": 13.405, "country": "Germany" } ] }""");
            }
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // "10115" + "DE": the US-only zippopotam path fails, and the worldwide
        // fallback must carry the country-code hint so Berlin's postal district
        // resolves (the spec's named scenario).
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "10115", null, null, null, "DE"));

        Assert.IsNotNull(snapshot);
        Assert.IsNotNull(searchUrl);
        Assert.IsTrue(searchUrl.Contains("countryCode=DE", StringComparison.OrdinalIgnoreCase), "The ZIP fallback must forward the CountryCode hint");
        Assert.AreEqual(52.52, snapshot.Lat);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_CityGeocode_ResolvesViaGeocodingApi()
    {
        string? searchUrl = null;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal))
            {
                searchUrl = url;
                return StubHttpHandler.Ok(SampleGeocode);
            }
            if (url.Contains("zippopotam", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleZip);
            if (url.Contains("/v1/forecast", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleForecast);
            return StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual("Berlin, Germany", snapshot.ResolvedCityName, "The resolved name must carry the country so a wrong pick is visible");
        Assert.AreEqual(52.52, snapshot.Lat);
        Assert.AreEqual(13.405, snapshot.Lon);
        Assert.AreEqual(2, stub.Calls, "Geocode + forecast must be exactly two calls");
        Assert.IsNotNull(searchUrl);
        Assert.IsTrue(searchUrl.Contains("count=10", StringComparison.Ordinal), "The geocoder must fetch 10 candidates for ranking");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_AmbiguousName_ExactMatchBeatsHigherPopulationFuzzy()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSameNameMultiCountry);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // "Victoria" must resolve to Victoria, Canada — not Vitoria, Brazil,
        // which the API ranks first by population (the reported bug).
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(48.4284, snapshot.Lat);
        Assert.AreEqual(-123.3656, snapshot.Lon);
        Assert.AreEqual("Victoria, British Columbia, Canada", snapshot.ResolvedCityName);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_StateSuffix_PicksMatchingAdmin1()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSpringfields);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // Missouri is listed first with more people; the ", MA" suffix must
        // pick Springfield, Massachusetts anyway.
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Springfield, MA", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(42.10148, snapshot.Lat);
        Assert.AreEqual(-72.58981, snapshot.Lon);
        Assert.AreEqual("Springfield, Massachusetts, United States", snapshot.ResolvedCityName);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_CountryCodeHint_FiltersToRequestedCountry()
    {
        var stub = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/v1/search", StringComparison.Ordinal))
            {
                Assert.IsTrue(
                    request.RequestUri.AbsoluteUri.Contains("countryCode=CR", StringComparison.OrdinalIgnoreCase),
                    "The CountryCode hint must be passed to the geocoding API");
                return StubHttpHandler.Ok(SampleSanJoses);
            }
            return request.RequestUri.AbsoluteUri.Contains("/v1/forecast", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(SampleForecast)
                : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // Identical city names in two countries: the CR hint must pick Costa Rica.
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "San Jose", null, null, null, "CR"));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(9.92807, snapshot.Lat);
        Assert.AreEqual(-84.09072, snapshot.Lon);
        Assert.AreEqual("San Jose, San José Province, Costa Rica", snapshot.ResolvedCityName);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_ZipGeocode_ResolvesViaZippopotam()
    {
        var stub = new StubHttpHandler(Respond);
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "10001", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(40.7505, snapshot.Lat);
        Assert.AreEqual(-73.9962, snapshot.Lon);
        Assert.AreEqual("New York City, New York", snapshot.ResolvedCityName);
        Assert.AreEqual(2, stub.Calls, "ZIP geocode + forecast must be exactly two calls");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_GeocodeFailure_LeavesCoordinatesUnresolved()
    {
        var stub = new StubHttpHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("/v1/forecast", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(SampleForecast)
                : StubHttpHandler.NotFound());
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Atlantis", null, null, null));

        Assert.IsNull(snapshot, "A failed geocode must leave coordinates unresolved so the widget shows its no-data state");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_GeocodeFailure_ThrottlesRetries()
    {
        // Regression guard for the flood: a failed geocode leaves coordinates
        // unresolved, and the render kick must not retry at frame rate — the
        // attempt time is stamped so the 5-minute throttle applies.
        var stub = new StubHttpHandler(_ => StubHttpHandler.NotFound());
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = CreateClient(stub, clock: clock);

        await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Atlantis", null, null, null));
        int callsAfterFirst = stub.Calls;
        Assert.IsTrue(callsAfterFirst >= 1, "The first attempt must hit the network");

        // Within the window, a non-forced fetch must be throttled away.
        await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Atlantis", null, null, null));
        Assert.AreEqual(callsAfterFirst, stub.Calls, "A failed geocode must cool down like a success");

        clock.Advance(TimeSpan.FromMinutes(6));
        await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Atlantis", null, null, null));
        Assert.IsTrue(stub.Calls > callsAfterFirst, "After the window, a retry is allowed");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_ForecastFailure_ThrottlesRetries()
    {
        // Regression guard: a forecast failure (geocode OK, forecast 500)
        // must cool down like a success — the render kick would otherwise
        // retry at frame rate during an outage (request + log storm).
        var stub = new StubHttpHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("/v1/search", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(SampleGeocode)
                : StubHttpHandler.NotFound());
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = CreateClient(stub, clock: clock);

        var first = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin", null, null, null));
        Assert.IsNull(first, "The forecast failure must return null");
        int callsAfterFirst = stub.Calls;

        await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin", null, null, null));
        Assert.AreEqual(callsAfterFirst, stub.Calls, "A failed forecast must cool down like a success");

        clock.Advance(TimeSpan.FromMinutes(6));
        await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin", null, null, null));
        Assert.IsTrue(stub.Calls > callsAfterFirst, "After the window, a retry is allowed");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_PersistedPickOnFreshInstance_ResolvesToPickedCandidate()
    {
        // Restart/import: candidates are in-memory per instance, so a stored
        // Location Match pick cannot resolve from cache. A fresh geocode must
        // promote the picked candidate to the winner instead of silently
        // reverting to the population ranking (the wrong-city bug returns).
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSameNameMultiCountry);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // Fresh instance, no prior geocode: the persisted pick must win over
        // the exact-name ranking (Victoria, Canada) and resolve to Vitoria.
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null)
        {
            LocationMatch = "Vitória, Espírito Santo, Brazil"
        });

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(-20.3194, snapshot.Lat);
        Assert.AreEqual(-40.3378, snapshot.Lon);
        Assert.AreEqual("Vitória, Espírito Santo, Brazil", snapshot.ResolvedCityName);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_ExplicitLatLonOverride_SkipsGeocoding()
    {
        var stub = new StubHttpHandler(Respond);
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
        var stub = new StubHttpHandler(Respond);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
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
        var stub = new StubHttpHandler(_ => StubHttpHandler.Ok(SampleForecast), gate);
        var client = CreateClient(stub);

        var inFlight = client.FetchCurrentAsync(CoordinateLocation);
        await TestWait.WaitUntilAsync(() => stub.Calls == 1, TimeSpan.FromSeconds(5));

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
        var stub = new StubHttpHandler(_ => fail ? StubHttpHandler.NotFound() : StubHttpHandler.Ok(SampleForecast));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var client = CreateClient(stub, clock: clock);

        var failed = await client.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNull(failed, "A failed fetch must yield null, not throw");

        // A failure cools down like a success (the retry-storm guard) — a
        // non-forced retry within the window must be throttled away.
        int callsAfterFirst = stub.Calls;
        fail = false;
        var throttled = await client.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNull(throttled, "A failed fetch must throttle retries within the window");
        Assert.AreEqual(callsAfterFirst, stub.Calls, "No retry may hit the network within the throttle window");

        // After the window elapses, the fetch runs and recovers.
        clock.Advance(TimeSpan.FromMinutes(6));
        var recovered = await client.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNotNull(recovered, "A subsequent fetch must succeed (the in-flight flag must be cleared)");
        Assert.AreEqual(12.5, recovered.CurrentTempC);
    }

    [TestMethod]
    public async Task InvalidateLocation_ResetsThrottle_SoNextFetchRuns()
    {
        var stub = new StubHttpHandler(Respond);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
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
        var stub = new StubHttpHandler(Respond);
        var writer = CreateClient(stub, cacheDirectory: dir);

        var fetched = await writer.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNotNull(fetched);

        var reader = new WeatherClient(dir, "weather_test.json", http: new HttpClient(new StubHttpHandler(Respond)));
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
    public async Task LoadCacheAsync_CacheWithoutName_DoesNotInventOne()
    {
        // A cache whose resolved name is missing must not be labeled "New York"
        // (the old fallback mislabeled any location) — the coordinates are the
        // only truthful identity a nameless cache carries.
        string dir = NewTempDir();
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "weather_test.json");
        await File.WriteAllTextAsync(path, """
        {
          "CurrentTempC": 12.5, "FeelsLikeC": 10.1, "Humidity": 60, "WindSpeedKmH": 8.2,
          "WeatherCode": 2, "HighTempC": 18, "LowTempC": 9, "ResolvedCityName": null,
          "Lat": 48.85, "Lon": 2.35, "DailyForecasts": [], "HourlyForecasts": []
        }
        """);

        var reader = new WeatherClient(dir, "weather_test.json", http: new HttpClient(new StubHttpHandler(Respond)));
        var loaded = await reader.LoadCacheAsync();

        Assert.IsNotNull(loaded);
        Assert.AreEqual("48.85, 2.35", loaded.ResolvedCityName,
            "A nameless cache must fall back to its coordinates, never a hardcoded city");
    }

    [TestMethod]
    public async Task LoadCacheAsync_NoCacheFile_ReturnsNull()
    {
        var client = new WeatherClient(NewTempDir(), "weather_test.json", http: new HttpClient(new StubHttpHandler(Respond)));

        var loaded = await client.LoadCacheAsync();

        Assert.IsNull(loaded);
    }

    [TestMethod]
    public async Task ClearCache_DeletesCacheFile()
    {
        string dir = NewTempDir();
        try
        {
            var stub = new StubHttpHandler(Respond);
            var writer = CreateClient(stub, cacheDirectory: dir);
            await writer.FetchCurrentAsync(CoordinateLocation);

            var reader = new WeatherClient(dir, "weather_test.json", http: new HttpClient(new StubHttpHandler(Respond)));
            // The freshly written cache file can land a moment after
            // FetchCurrentAsync returns (async flush / AV scanner), so wait
            // for it before asserting it exists — same retry pattern as the
            // delete loop below.
            for (int attempt = 0; attempt < 20 && await reader.LoadCacheAsync() is null; attempt++)
            {
                await Task.Delay(50);
            }
            Assert.IsNotNull(await reader.LoadCacheAsync());

            // The freshly written file can be held open by the AV scanner for a
            // moment, so ClearCache's delete may need a retry before it sticks.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                reader.ClearCache();
                if (await reader.LoadCacheAsync() is null) return;
                await Task.Delay(50);
            }

            Assert.Fail("After ClearCache the cache file must be gone (delete kept failing)");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
