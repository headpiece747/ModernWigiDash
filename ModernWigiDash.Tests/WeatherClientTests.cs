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
      "current": { "temperature_2m": 12.5, "relative_humidity_2m": 60, "apparent_temperature": 10.1, "weather_code": 2, "wind_speed_10m": 8.2, "time": "2026-08-07T12:00" },
      "hourly": {
        "time": ["2026-08-07T00:00", "2026-08-07T01:00"],
        "temperature_2m": [12.5, 13.1],
        "relative_humidity_2m": [40, 45],
        "weather_code": [2, 2]
      },
      "daily": {
        "time": ["2026-08-07", "2026-08-08"],
        "weather_code": [2, 3],
        "temperature_2m_max": [18.0, 20.0],
        "temperature_2m_min": [9.0, 11.0]
      }
    }
    """;

    // The legacy response shape (current_weather + relativehumidity_2m +
    // weathercode) must still parse — stale caches and edge responses carry it.
    private const string SampleForecastLegacy = """
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

    // Two same-named cities across countries: the exact-name match must beat
    // the higher-population fuzzy match (the Vitoria/Victoria bug).
    internal const string SampleSameNameMultiCountry = """
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
    internal const string SampleSanJoses = """
    {
      "results": [
        { "name": "San Jose", "latitude": 37.33939, "longitude": -121.89496, "admin1": "California", "country": "United States", "country_code": "US", "population": 1026908 },
        { "name": "San Jose", "latitude": 9.92807, "longitude": -84.09072, "admin1": "San Jos\u00e9 Province", "country": "Costa Rica", "country_code": "CR", "population": 335007 }

      ]
    }
    """;

    // The real Open-Meteo candidate set for a bare "Berlin" (captured from the
    // live API): five places share the exact name, Germany has the population —
    // so without a suffix or country hint the population tiebreak picks Berlin,
    // Germany (the reported on-device symptom: a US Berlin user saw Berlin DE's
    // weather). The suffix and country-hint tests below pin the escape routes.
    internal const string SampleBerlines = """
    {
      "results": [
        { "name": "Berlin", "admin1": "State of Berlin", "country": "Germany", "country_code": "DE", "population": 3426354, "latitude": 52.52437, "longitude": 13.41053 },
        { "name": "Berlin", "admin1": "New Hampshire", "country": "United States", "country_code": "US", "population": 9367, "latitude": 44.46867, "longitude": -71.18508 },
        { "name": "Berlin", "admin1": "New Jersey", "country": "United States", "country_code": "US", "population": 7590, "latitude": 39.79123, "longitude": -74.92905 },
        { "name": "Brunswick", "admin1": "Maryland", "country": "United States", "country_code": "US", "population": 6116, "latitude": 39.31427, "longitude": -77.62777 },
        { "name": "Berlin", "admin1": "Wisconsin", "country": "United States", "country_code": "US", "population": 5420, "latitude": 43.96804, "longitude": -88.94345 }
      ]
    }
    """;

    // The real zippopotam shape: the place (with string coordinates) lives
    // under "places[0]" — the fixture mirrors the live API, not a hand-made
    // root-level shape (the earlier root-level numeric fixture let the parser
    // drift from the API, so real ZIPs silently fell back).
    private const string SampleZip = """
    {
      "country": "United States",
      "post code": "10001",
      "places": [
        {
          "place name": "New York City",
          "longitude": "-73.9962",
          "latitude": "40.7505",
          "state": "New York"
        }
      ]
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
        Assert.AreEqual("00:00", snapshot.HourlyForecasts[0].TimeLabel);
        Assert.AreEqual("40.71, -74.00", snapshot.ResolvedCityName);
        Assert.AreEqual(40.71, snapshot.Lat);
        Assert.AreEqual(-74.00, snapshot.Lon);
        Assert.AreEqual(1, stub.Calls, "A coordinate-pair location must skip geocoding entirely");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_HumidityAndFeelsLike_ComeFromCurrentBlockNotMidnightBucket()
    {
        // Precision regression: the hourly array starts at local midnight, so
        // its first humidity bucket is hours stale. The current block's
        // relative_humidity_2m (15-min precision) must win, and
        // apparent_temperature must actually parse (the legacy current_weather
        // block never carried it).
        var stub = new StubHttpHandler(Respond);
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(CoordinateLocation);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(60, snapshot.Humidity, "humidity must come from the current block (60), not the midnight hourly bucket (40)");
        Assert.AreEqual(10.1, snapshot.FeelsLikeC, "feels-like must come from the current block's apparent_temperature");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_LegacyResponseShape_StillParses()
    {
        // Stale caches / legacy responses carry current_weather +
        // relativehumidity_2m + weathercode; they must parse (with the
        // by-hours-stale humidity as the only option) rather than fail.
        var stub = new StubHttpHandler(request =>
            request.RequestUri!.AbsoluteUri.Contains("/v1/forecast", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(SampleForecastLegacy)
                : StubHttpHandler.NotFound());
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(CoordinateLocation);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(12.5, snapshot.CurrentTempC);
        Assert.AreEqual(60, snapshot.Humidity, "legacy shape must fall back to the hourly bucket");
        Assert.AreEqual(2, snapshot.WeatherCode);
        Assert.AreEqual(2, snapshot.DailyForecasts!.Count);
        Assert.AreEqual(2, snapshot.HourlyForecasts!.Count);
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
    public async Task FetchCurrentAsync_CancelledToken_PropagatesOperationCanceled()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Ok(SampleForecast));
        var client = CreateClient(stub);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Teardown cancels the widget's poll CTS: the cancellation must
        // propagate through the geocode leg — never be swallowed and logged
        // as a fetch failure (the request may still be dispatched before the
        // pipeline observes the token; the contract is the propagation).
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null), cancellationToken: cts.Token));
    }

    [TestMethod]
    public async Task FetchCurrentAsync_AmbiguousBareName_ReturnsNullWithoutFetching()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // A bare "Berlin" ties four candidates on the exact name; without a pick
        // the population choice is untrustworthy — wrong data must never display.
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin", null, null, null));

        Assert.IsNull(snapshot, "an ambiguous bare name must not fetch weather");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_AmbiguousName_WithLocationMatch_FetchesThePick()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // A persisted Location Match pick resolves the tie deterministically.
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation(
            "Fixed Location", "Berlin", null, null, null)
        { LocationMatch = "Berlin, New Hampshire, United States" });

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(44.46867, snapshot.Lat, 0.0001, "the picked Berlin, NH must win over the population choice");
        Assert.AreEqual(-71.18508, snapshot.Lon, 0.0001);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_UnambiguousName_FetchesTheWinner()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSameNameMultiCountry);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // "Victoria" ties no candidate on the exact name — the exact-match winner
        // is unambiguous and fetches instantly.
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null));

        Assert.IsNotNull(snapshot);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_AmbiguousBareName_StateSuffixPicksTheUsBerlin()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // The full-state suffix "Berlin, New Hampshire" must pick Berlin, NH —
        // the suffix beats the population tiebreak (note: the two-letter
        // abbreviation "NH" does NOT match — the abbreviation support only
        // covers state names that start with their abbreviation, for example "MA"
        // the working escape routes are the full state name, the CountryCode
        // hint, or the Location Match dropdown).
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin, New Hampshire", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(44.46867, snapshot.Lat);
        Assert.AreEqual(-71.18508, snapshot.Lon);
        Assert.AreEqual("Berlin, New Hampshire, United States", snapshot.ResolvedCityName);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_AmbiguousBareName_CountryHintTie_StillReturnsNull()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // A country hint that leaves multiple candidates tied is still a
        // population-decided tie: bare "Berlin" + US ties Berlin NH/NJ/WI on
        // the hint, and the population choice (NH) is exactly the untrustworthy
        // winner the gate exists to block — the pick must come from the
        // "Location Match" dropdown. (The hint still disambiguates when it
        // leaves a single winner, e.g. San Jose + CR.)
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin", null, null, null, "US"));

        Assert.IsNull(snapshot, "a hint that leaves multiple candidates tied must not fetch weather");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_FullLabelSuffix_PicksTheUniquePlace()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // The full label "Berlin, New Hampshire, United States" (what a pick
        // persists) must resolve deterministically: both suffix components match
        // Berlin NH only — the population tiebreak must not come into play.
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin, New Hampshire, United States", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(44.46867, snapshot.Lat, 0.0001);
        Assert.AreEqual(-71.18508, snapshot.Lon, 0.0001);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_TwoPartStateAndCountryLabel_MatchesAdmin1AndCountry()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin, New Hampshire, United States", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(44.46867, snapshot.Lat, 0.0001, "admin1 'New Hampshire' and country 'United States' must both match");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_LabelWithNonMatchingComponent_DoesNotResolveToIt()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // "Berlin, Ontario, United States": no candidate has admin1/country
        // "Ontario" — every component must match, so no suffix score; the bare
        // name tie then flags ambiguity (no fetch).
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin, Ontario, United States", null, null, null));

        Assert.IsNull(snapshot, "a non-matching suffix component must not resolve to a population pick");
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
    public async Task LoadCacheAsync_LazyFileName_RoundTrips()
    {
        // The provider-based constructor resolves the file name at each
        // load/save — a writer and reader sharing the provider must round-trip
        // the same derived file (the widget's placed-InstanceId keying path).
        string dir = NewTempDir();
        var stub = new StubHttpHandler(Respond);
        var writer = new WeatherClient(dir, () => "weather_lazy.json", http: new HttpClient(stub));

        var fetched = await writer.FetchCurrentAsync(CoordinateLocation);
        Assert.IsNotNull(fetched);

        var reader = new WeatherClient(dir, () => "weather_lazy.json", http: new HttpClient(new StubHttpHandler(Respond)));
        var loaded = await reader.LoadCacheAsync();

        Assert.IsNotNull(loaded);
        Assert.AreEqual(12.5, loaded.CurrentTempC);
        Assert.AreEqual("40.71, -74.00", loaded.ResolvedCityName);
    }

    [TestMethod]
    public void CacheFileName_Resolves_Lazily_FromProvider()
    {
        string name = "first.json";
        var client = new WeatherClient(NewTempDir(), () => name);

        Assert.AreEqual("first.json", client.CacheFileName);

        // The name is read at resolution time, not baked at construction.
        name = "second.json";
        Assert.AreEqual("second.json", client.CacheFileName);
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

    [TestMethod]
    public async Task FetchCurrentAsync_ResolvedWinner_ExposesPopulation()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Berlin, New Hampshire, United States", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(9367, client.LastResolvedPopulation, 0.0001);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_WarmLocationMatchPick_ExposesPickedPopulation()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSameNameMultiCountry);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // First resolution populates the candidates; the warm in-memory pick
        // path resolves against them (no re-geocode).
        await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null));
        string picked = client.LastCandidates[^1].Label; // Vitoria, Brazil (population 1962476)
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Victoria", null, null, null) { LocationMatch = picked }, force: true);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(1962476, client.LastResolvedPopulation, 0.0001,
            "the warm in-memory pick path must expose the picked candidate's population");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_PartialSuffixMatch_TiesAndReturnsNull()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleSanJoses);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // "California" matches only the US candidate's admin1; "Germany"
        // matches neither. The all-or-nothing rule scores the whole suffix 0
        // for both — a partial-sum tiebreak must NOT resolve this uniquely.
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "San Jose, California, Germany", null, null, null));

        Assert.IsNull(snapshot, "a partial suffix match must not resolve — the suffix is all-or-nothing");
    }

    // The renamed-country shape: the geocoder reports the official country
    // name ("The Netherlands") while users type the common English name
    // ("Netherlands") — the contains tier of the suffix matcher must keep
    // the NL Amsterdam the unique winner over the same-named US towns.
    internal const string SampleAmsterdams = """
    {
      "results": [
        { "name": "Amsterdam", "admin1": "North Holland", "country": "The Netherlands", "country_code": "NL", "population": 741636, "latitude": 52.37403, "longitude": 4.88969 },
        { "name": "Amsterdam", "admin1": "New York", "country": "United States", "country_code": "US", "population": 18620, "latitude": 42.93869, "longitude": -74.18819 },
        { "name": "Amsterdam", "admin1": "Ohio", "country": "United States", "country_code": "US", "population": 510, "latitude": 40.47368, "longitude": -80.92287 }
      ]
    }
    """;

    [TestMethod]
    public async Task FetchCurrentAsync_RenamedCountrySuffix_ResolvesTheCommonEnglishName()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleAmsterdams);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // "Netherlands" is not an exact or prefix match of the geocoder's
        // "The Netherlands" — the contains tier must still pick it over the
        // US Amsterdams (before the tier, every candidate tied at 1000 and
        // the suffix gated: "Amsterdam, Netherlands" never resolved).
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Amsterdam, Netherlands", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(52.37403, snapshot.Lat, 0.0001);
        Assert.AreEqual(4.88969, snapshot.Lon, 0.0001);
        Assert.AreEqual("Amsterdam, North Holland, The Netherlands", snapshot.ResolvedCityName);
    }

    // The duplicate-entry shape that gates capitals: the geocoder lists the
    // capital plus same-named towns in the SAME country — "Accra, Ghana"
    // ties across two GH entries unless the same-country population
    // tiebreak picks the city (1.96M) over the nameless town (no population).
    internal const string SampleAccras = """
    {
      "results": [
        { "name": "Accra", "latitude": 5.55602, "longitude": -0.1969, "admin1": "Greater Accra Region", "country": "Ghana", "country_code": "GH", "population": 1963264 },
        { "name": "Accra", "latitude": 6.10000, "longitude": -2.80000, "admin1": "Western North", "country": "Ghana", "country_code": "GH" }
      ]
    }
    """;

    [TestMethod]
    public async Task FetchCurrentAsync_SameCountryDuplicateTie_PicksThePopulatedCity()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleAccras);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        // Both candidates tie at the top score with the same name in the
        // same country — the same-country tiebreak must pick the populated
        // city, or "Accra, Ghana" would never resolve.
        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Accra, Ghana", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(5.55602, snapshot.Lat, 0.0001);
        Assert.AreEqual(-0.1969, snapshot.Lon, 0.0001);
        Assert.AreEqual("Accra, Greater Accra Region, Ghana", snapshot.ResolvedCityName);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_SameCountryTieWithoutPopulation_StillGates()
    {
        // Two same-country ties with NO population anywhere: nothing
        // distinguishes them, so the ambiguity gate must hold (the pick
        // dropdown remains the escape).
        const string fixture = """
        {
          "results": [
            { "name": "Accra", "latitude": 5.55602, "longitude": -0.1969, "admin1": "Greater Accra Region", "country": "Ghana", "country_code": "GH" },
            { "name": "Accra", "latitude": 6.10000, "longitude": -2.80000, "admin1": "Western North", "country": "Ghana", "country_code": "GH" }
          ]
        }
        """;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(fixture);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Accra, Ghana", null, null, null));

        Assert.IsNull(snapshot, "a same-country tie with no population must not resolve");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_RenamedCountryAlias_ResolvesTheCommonEnglishName()
    {
        // The geocoder reports "Republic of Türkiye"; the user types
        // "Turkey" — letters differ, so even the contains tier cannot reach
        // it; the alias table must. The Madagascar "Ankara" keeps the test
        // honest: without the alias, both tie at the bare-name score.
        const string fixture = """
        {
          "results": [
            { "name": "Ankara", "latitude": 39.93336, "longitude": 32.85974, "admin1": "Ankara", "country": "Republic of Türkiye", "country_code": "TR", "population": 3517182 },
            { "name": "Ankara", "latitude": -24.80000, "longitude": 45.20000, "admin1": "Androy Region", "country": "Madagascar", "country_code": "MG" }
          ]
        }
        """;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(fixture);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Ankara, Turkey", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(39.93336, snapshot.Lat, 0.0001);
        Assert.AreEqual(32.85974, snapshot.Lon, 0.0001);
        Assert.AreEqual("Ankara, Ankara, Republic of Türkiye", snapshot.ResolvedCityName);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_SuffixTieWithoutSuffixMatch_StillGates()
    {
        // "Washington, District of Columbia": the DC candidate's name is
        // "Washington D.C." (no exact-name points), so the state Washingtons
        // tie at the bare score — the suffix matched NOBODY in the tie, and
        // the population tiebreak must NOT pick Washington, PA for a user who
        // asked for DC.
        const string fixture = """
        {
          "results": [
            { "name": "Washington D.C.", "latitude": 38.89511, "longitude": -77.03637, "admin1": "District of Columbia", "country": "United States", "country_code": "US", "population": 689545 },
            { "name": "Washington", "latitude": 40.17396, "longitude": -80.24617, "admin1": "Pennsylvania", "country": "United States", "country_code": "US", "population": 13176 },
            { "name": "Washington", "latitude": 35.54655, "longitude": -77.05217, "admin1": "North Carolina", "country": "United States", "country_code": "US", "population": 9854 }
          ]
        }
        """;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(fixture);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Washington, District of Columbia", null, null, null));

        Assert.IsNull(snapshot, "a suffix that matched no tied candidate must not let population pick a wrong city");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_DiacriticCapitalTie_PicksThePopulatedCity()
    {
        // The geocoder lists the accented capital twice within Paraguay
        // (city + duplicate entry): both tie on the suffix at 500 — the
        // same-country tiebreak must pick the populated one, or
        // "Asuncion, Paraguay" (ASCII spelling) never resolves.
        const string fixture = """
        {
          "results": [
            { "name": "Asunci\u00f3n", "latitude": -25.26374, "longitude": -57.57593, "admin1": "Asuncion", "country": "Paraguay", "country_code": "PY", "population": 1482200 },
            { "name": "Asunci\u00f3n", "latitude": -25.28000, "longitude": -57.63000, "admin1": "Asuncion", "country": "Paraguay", "country_code": "PY" }
          ]
        }
        """;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(fixture);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Asuncion, Paraguay", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(-25.26374, snapshot.Lat, 0.0001);
        Assert.AreEqual(-57.57593, snapshot.Lon, 0.0001);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_ZipWithCountryHint_RoutesToThatCountrysZipService()
    {
        // 10115 is both Berlin's postal district and a valid Manhattan ZIP:
        // the DE hint must route to zippopotam's /de/ service (Berlin), never
        // the /us/ default (which would resolve New York City).
        const string berlinZip = """
        {
          "country": "Germany",
          "post code": "10115",
          "places": [
            { "place name": "Berlin", "longitude": "13.3922", "latitude": "52.532", "state": "Berlin" }
          ]
        }
        """;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("zippopotam.us/de/", StringComparison.Ordinal)) return StubHttpHandler.Ok(berlinZip);
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "10115", null, null, null, "DE"));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(52.532, snapshot.Lat, 0.0001, "the DE hint must route the ZIP to the /de/ service");
        Assert.AreEqual(13.3922, snapshot.Lon, 0.0001);
        Assert.AreEqual("Berlin, Berlin", snapshot.ResolvedCityName);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_TerritorySuffix_ResolvesViaTheCountryCode()
    {
        // US-territory candidates carry an EMPTY country field with only the
        // code ("San Juan" is PR) — "San Juan, Puerto Rico" must resolve via
        // the alias to the PR code, not tie with the same-named cities. The
        // Dominican "San Juan Province" traps the alias: the PR code must
        // never substring-match "Province".
        const string fixture = """
        {
          "results": [
            { "name": "San Juan", "latitude": 18.46554, "longitude": -66.10574, "admin1": "San Juan", "country": "", "country_code": "PR", "population": 418140 },
            { "name": "San Juan", "latitude": -31.53750, "longitude": -68.53639, "admin1": "San Juan", "country": "Argentina", "country_code": "AR", "population": 109123 },
            { "name": "San Juan", "latitude": 26.18924, "longitude": -98.15529, "admin1": "Texas", "country": "United States", "country_code": "US", "population": 36556 },
            { "name": "San Juan", "latitude": 18.81000, "longitude": -71.23000, "admin1": "San Juan Province", "country": "Dominican Republic", "country_code": "DO", "population": 72950 }
          ]
        }
        """;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(fixture);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "San Juan, Puerto Rico", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(18.46554, snapshot.Lat, 0.0001, "the PR alias must resolve over the same-named AR/US cities");
        Assert.AreEqual(-66.10574, snapshot.Lon, 0.0001);
    }

    [TestMethod]
    public async Task FetchCurrentAsync_DiacriticCapital_BeatsSameNamedAsciiTowns()
    {
        // The geocoder's top-10 for "Asuncion" includes unaccented towns in
        // the Philippines — without diacritic-insensitive matching they win
        // the exact-name bonus over the accented Paraguayan capital, and
        // "Asuncion, Paraguay" never resolves.
        const string fixture = """
        {
          "results": [
            { "name": "Asuncion", "latitude": 15.69390, "longitude": 120.81290, "admin1": "Central Luzon", "country": "Philippines", "country_code": "PH" },
            { "name": "Asuncion", "latitude": 9.60000, "longitude": 125.60000, "admin1": "Eastern Visayas", "country": "Philippines", "country_code": "PH" },
            { "name": "Asunci\u00f3n", "latitude": -25.26374, "longitude": -57.57593, "admin1": "Asuncion", "country": "Paraguay", "country_code": "PY", "population": 1482200 }
          ]
        }
        """;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(fixture);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var client = CreateClient(stub);

        var snapshot = await client.FetchCurrentAsync(new WeatherLocation("Fixed Location", "Asuncion, Paraguay", null, null, null));

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(-25.26374, snapshot.Lat, 0.0001, "the ASCII spelling must resolve the accented capital, not a same-named PH town");
        Assert.AreEqual(-57.57593, snapshot.Lon, 0.0001);
        Assert.AreEqual("Asunción, Asuncion, Paraguay", snapshot.ResolvedCityName);
    }

    [TestMethod]
    public async Task SearchCitiesAsync_MapsCandidatesWithPopulation()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Ok(SampleBerlines));
        var client = CreateClient(stub);

        var results = await client.SearchCitiesAsync("Berl", CancellationToken.None);

        Assert.AreEqual(5, results.Count);
        var first = results[0];
        Assert.AreEqual("Berlin, State of Berlin, Germany", first.Label);
        Assert.AreEqual(52.52437, first.Lat, 0.0001);
        Assert.AreEqual(3426354, first.Population);
    }

    [TestMethod]
    public async Task SearchCitiesAsync_HttpError_ReturnsEmptyList()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.NotFound());
        var client = CreateClient(stub);

        var results = await client.SearchCitiesAsync("Berlin", CancellationToken.None);

        Assert.IsNotNull(results);
        Assert.AreEqual(0, results.Count, "a failed search must not throw");
    }

    [TestMethod]
    public async Task SearchCitiesAsync_Cancelled_ThrowsOperationCanceled()
    {
        var stub = new StubHttpHandler(_ => StubHttpHandler.Ok(SampleBerlines));
        var client = CreateClient(stub);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.SearchCitiesAsync("Berlin", cts.Token));
    }
}
