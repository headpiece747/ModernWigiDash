using System.Net.Http;

namespace ModernWigiDash.Tests;

/// <summary>
/// The geocoder's single resolution entry: the one place the client asks
/// "where is this" — the whole resolution ladder (explicit coordinates, a
/// "lat,lon" pair, a postal code via the zippopotam route + the geocoder's
/// postal fallback, a dropdown pick, the city-name geocode) spelled once
/// behind the door, plus the forecast-URL read leg that owns the one
/// geocoding URL the client no longer builds. The resolver's DECISION rules
/// stay pinned in WeatherLocationResolverTests; this file pins the door's
/// routing, the outcome shapes, and the HTTP leg(s) each leg performs.
/// </summary>
[TestClass]
public class WeatherGeocoderResolveTests
{
    /// <summary>The single-Berlin winner the stubbed city search returns
    /// (one candidate — the resolver's unique-winner path).</summary>
    private const string BerlinBody = """
    {"results":[{"name":"Berlin","admin1":"State of Berlin","country":"Germany","country_code":"DE","population":3426354,"latitude":52.52437,"longitude":13.41053}]}
    """;

    private static (WeatherGeocoder Geocoder, StubHttpHandler Handler) Geocode(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        var client = new HttpClient(handler);
        return (new WeatherGeocoder(() => client), handler);
    }

    private static GeocodeCandidate BerlinCandidate =>
        new("Berlin, Germany", "Berlin, Germany", 52.52437, 13.41053) { Population = 3426354 };

    [TestMethod]
    public async Task Resolve_ExplicitCoordinates_WinOverStalePick_ResolvedFromCoordinates()
    {
        // Explicit coordinates are authoritative — a stale Location Match
        // pick from a previous city query must not win, and no leg may run.
        var (geocoder, handler) = Geocode(_ => throw new InvalidOperationException("no fetch expected"));
        var location = new WeatherLocation("Coordinates", "Berlin", "40.7100", "-74.0000", null)
        { LocationMatch = "Berlin, Germany" };

        var result = await geocoder.ResolveAsync(location, [BerlinCandidate], CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Resolved));
        var resolved = (WeatherResolutionOutcome.Resolved)result;
        Assert.AreEqual(40.71, resolved.Lat);
        Assert.AreEqual(-74.0, resolved.Lon);
        Assert.AreEqual(WeatherLocationResolver.FormatCoordinates(40.71, -74.0), resolved.Label);
        Assert.AreEqual(0.0, resolved.Population);
        Assert.IsNull(resolved.RefreshedCandidates, "a non-city leg must leave the dropdown untouched");
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Resolve_ExplicitCoordinates_WithCustomLabel_LabelIsCustomLabel()
    {
        var (geocoder, handler) = Geocode(_ => throw new InvalidOperationException("no fetch expected"));
        var location = new WeatherLocation("Coordinates", "Berlin", "40.7100", "-74.0000", "My Spot");

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        Assert.AreEqual("My Spot", ((WeatherResolutionOutcome.Resolved)result).Label);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Resolve_ExplicitCoordinates_NaNCoordinate_FallsThroughToCityGeocode()
    {
        // "NaN" parses as a valid double — the coordinate-range check (not
        // the parse) is what must reject it and route to the query geocode.
        var (geocoder, handler) = Geocode(url => url.RequestUri!.ToString().Contains("open-meteo")
            ? StubHttpHandler.Ok(BerlinBody)
            : throw new InvalidOperationException($"unexpected leg: {url.RequestUri}"));
        var location = new WeatherLocation("Coordinates", "Berlin", "NaN", "-74.0000", null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Resolved));
        Assert.AreEqual(52.52437, ((WeatherResolutionOutcome.Resolved)result).Lat);
        Assert.AreEqual(1, handler.Calls, "only the city search may run");
    }

    [TestMethod]
    public async Task Resolve_CoordinatePairInLocation_ResolvedWithoutHttp()
    {
        var (geocoder, handler) = Geocode(_ => throw new InvalidOperationException("no fetch expected"));
        var location = new WeatherLocation("Coordinates", "52.52,13.41", null, null, null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        var resolved = (WeatherResolutionOutcome.Resolved)result;
        Assert.AreEqual(52.52, resolved.Lat);
        Assert.AreEqual(13.41, resolved.Lon);
        Assert.AreEqual(WeatherLocationResolver.FormatCoordinates(52.52, 13.41), resolved.Label, "the pair label is the default coordinate format");
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Resolve_ZipCode_RoutesToZippopotam_ResolvedFromPlace()
    {
        var (geocoder, handler) = Geocode(url =>
            url.RequestUri!.ToString() == WeatherLocationResolver.BuildZipLookupUri("10001", "us").ToString()
                ? StubHttpHandler.Ok(WeatherTestData.SampleZip)
                : throw new InvalidOperationException($"unexpected leg: {url.RequestUri}"));
        var location = new WeatherLocation("Zip", "10001", null, null, null) { CountryCode = "us" };

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        var resolved = (WeatherResolutionOutcome.Resolved)result;
        Assert.AreEqual(40.7505, resolved.Lat);
        Assert.AreEqual(-73.9962, resolved.Lon);
        Assert.AreEqual("New York City, New York", resolved.Label);
        Assert.AreEqual(1, handler.Calls, "exactly the zippopotam leg");
        Assert.AreEqual(WeatherLocationResolver.BuildZipLookupUri("10001", "us").ToString(), handler.RequestUrls[0]);
    }

    [TestMethod]
    public async Task Resolve_ZipLookupFailure_FallsBackToCityGeocode()
    {
        // The zippopotam route 404s — the door must fall back to the geocoder's
        // postal search WITH the country hint (the first fallback leg), so
        // Berlin's postal district resolves.
        var (geocoder, handler) = Geocode(url =>
        {
            var uri = url.RequestUri!.ToString();
            if (uri == WeatherLocationResolver.BuildZipLookupUri("10115", "de").ToString()) return StubHttpHandler.NotFound();
            if (uri == WeatherLocationResolver.BuildSearchUri("10115", "de").ToString()) return StubHttpHandler.Ok(BerlinBody);
            throw new InvalidOperationException($"unexpected leg: {uri}");
        });
        var location = new WeatherLocation("Zip", "10115", null, null, null) { CountryCode = "de" };

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Resolved));
        Assert.AreEqual(52.52437, ((WeatherResolutionOutcome.Resolved)result).Lat);
        Assert.AreEqual(2, handler.Calls, "the zip leg is attempted before the city fallback");
        Assert.AreEqual(WeatherLocationResolver.BuildZipLookupUri("10115", "de").ToString(), handler.RequestUrls[0]);
        Assert.AreEqual(WeatherLocationResolver.BuildSearchUri("10115", "de").ToString(), handler.RequestUrls[1]);
    }

    [TestMethod]
    public async Task Resolve_PostalZipPlus4_NoHint_UsRouteFiveDigitLookup()
    {
        // "10001-1234" without a hint routes US: zippopotam's US index is
        // 5-digit only, so the lookup key is the leading 5 digits and no
        // fallback leg must run.
        var (geocoder, handler) = Geocode(url =>
            url.RequestUri!.ToString() == WeatherLocationResolver.BuildZipLookupUri("10001", "us").ToString()
                ? StubHttpHandler.Ok(WeatherTestData.SampleZip)
                : throw new InvalidOperationException($"unexpected leg: {url.RequestUri}"));
        var location = new WeatherLocation("Zip", "10001-1234", null, null, null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        var resolved = (WeatherResolutionOutcome.Resolved)result;
        Assert.AreEqual(40.7505, resolved.Lat);
        Assert.AreEqual("New York City, New York", resolved.Label);
        Assert.AreEqual(1, handler.Calls, "the -4 delivery suffix must not add legs");
        Assert.AreEqual(WeatherLocationResolver.BuildZipLookupUri("10001", "us").ToString(), handler.RequestUrls[0]);
    }

    [TestMethod]
    public async Task Resolve_PostalGbCode_GbHint_ShortFormLookup()
    {
        // The GB index keys on the 3-char outward code: "M1 1AA" must look up
        // as GB/M11 (the full code 404s against the live API — live-probed).
        var (geocoder, handler) = Geocode(url =>
            url.RequestUri!.ToString() == WeatherLocationResolver.BuildZipLookupUri("M11", "gb").ToString()
                ? StubHttpHandler.Ok(WeatherTestData.SampleZipGbM11)
                : throw new InvalidOperationException($"unexpected leg: {url.RequestUri}"));
        var location = new WeatherLocation("Zip", "M1 1AA", null, null, null) { CountryCode = "GB" };

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        var resolved = (WeatherResolutionOutcome.Resolved)result;
        Assert.AreEqual(53.4809, resolved.Lat);
        Assert.AreEqual("Manchester, England", resolved.Label);
        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(WeatherLocationResolver.BuildZipLookupUri("M11", "gb").ToString(), handler.RequestUrls[0]);
    }

    [TestMethod]
    public async Task Resolve_PostalAlphanumericWithoutHint_KeepsCityLeg()
    {
        // An alphanumeric postal code with no country hint is not routable
        // (guessing US is a coin flip): it keeps the city leg — one search,
        // no zippopotam route, and an empty geocode is Unresolved, not a throw.
        var (geocoder, handler) = Geocode(url =>
        {
            var uri = url.RequestUri!.ToString();
            if (uri == WeatherLocationResolver.BuildSearchUri("M1 1AA", null).ToString())
                return StubHttpHandler.Ok("""{ "generationtime_ms": 1.0 }""");
            throw new InvalidOperationException($"unexpected leg: {uri}");
        });
        var location = new WeatherLocation("Zip", "M1 1AA", null, null, null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Unresolved));
        Assert.AreEqual(1, handler.Calls, "city leg only — no zippopotam route for a hintless alphanumeric code");
        Assert.AreEqual(WeatherLocationResolver.BuildSearchUri("M1 1AA", null).ToString(), handler.RequestUrls[0]);
    }

    [TestMethod]
    public async Task Resolve_PostalFallback_Zippopotam404_HintedEmptyThenBare_ResolvedFromBareLeg()
    {
        // The DE route 404s, the hinted postal search finds nothing in the
        // geocoder's index, and the bare search resolves the US reading: the
        // verdict must come from the last leg (3 legs: zippopotam, hinted
        // search, bare search) — a hint the index lacks must not leave the
        // user with no weather the geocoder could have resolved.
        var (geocoder, handler) = Geocode(url =>
        {
            var uri = url.RequestUri!.ToString();
            if (uri == WeatherLocationResolver.BuildZipLookupUri("99999", "de").ToString()) return StubHttpHandler.NotFound();
            if (uri == WeatherLocationResolver.BuildSearchUri("99999", "DE").ToString())
                return StubHttpHandler.Ok("""{ "generationtime_ms": 1.0 }""");
            if (uri == WeatherLocationResolver.BuildSearchUri("99999", null).ToString())
                return StubHttpHandler.Ok(WeatherTestData.SamplePostalSingleTown);
            throw new InvalidOperationException($"unexpected leg: {uri}");
        });
        var location = new WeatherLocation("Zip", "99999", null, null, null) { CountryCode = "DE" };

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        var resolved = (WeatherResolutionOutcome.Resolved)result;
        Assert.AreEqual(32.96593, resolved.Lat);
        Assert.AreEqual("Addison, Texas, United States", resolved.Label);
        Assert.AreEqual(3, handler.Calls, "zippopotam + hinted search + bare search");
        Assert.AreEqual(WeatherLocationResolver.BuildZipLookupUri("99999", "de").ToString(), handler.RequestUrls[0]);
        Assert.AreEqual(WeatherLocationResolver.BuildSearchUri("99999", "DE").ToString(), handler.RequestUrls[1]);
        Assert.AreEqual(WeatherLocationResolver.BuildSearchUri("99999", null).ToString(), handler.RequestUrls[2]);
    }

    [TestMethod]
    public async Task Resolve_PostalFallback_HintedLegAnswers_SequenceStops()
    {
        // A hinted postal search that returns candidates is the complete
        // verdict — the bare retry must not run (2 legs total). The search
        // leg carries the hint VERBATIM from the location (case kept — the
        // geocoder treats the parameter case-insensitively; only the
        // zippopotam route is lowercased).
        const string paris = """
        { "results": [ { "name": "Paris", "admin1": "Ile-de-France Region", "country": "France", "country_code": "FR", "population": 2138551, "latitude": 48.85341, "longitude": 2.3488 } ] }
        """;
        var (geocoder, handler) = Geocode(url =>
        {
            var uri = url.RequestUri!.ToString();
            if (uri == WeatherLocationResolver.BuildZipLookupUri("75001", "fr").ToString()) return StubHttpHandler.NotFound();
            if (uri == WeatherLocationResolver.BuildSearchUri("75001", "FR").ToString()) return StubHttpHandler.Ok(paris);
            throw new InvalidOperationException($"unexpected leg: {uri}");
        });
        var location = new WeatherLocation("Zip", "75001", null, null, null) { CountryCode = "FR" };

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        var resolved = (WeatherResolutionOutcome.Resolved)result;
        Assert.AreEqual(48.85341, resolved.Lat);
        Assert.AreEqual("Paris, Ile-de-France Region, France", resolved.Label);
        Assert.AreEqual(2, handler.Calls, "a hinted leg that answered must not trigger the bare retry");
    }

    [TestMethod]
    public async Task Resolve_PostalFallback_CrossCountryTie_AmbiguousCarriesEveryCandidate()
    {
        // A postal code shared across countries (75001: Paris FR + Addison
        // US): no hint, the US route 404s, the bare search returns the
        // collision — no candidate bears the code's "name", so the ranking
        // ties at zero and the gate holds (no-guess). A postal query's pick
        // list keeps EVERY candidate (there is no exact-name row to filter
        // to) — the tie is only escapable through them.
        var (geocoder, handler) = Geocode(url =>
        {
            var uri = url.RequestUri!.ToString();
            if (uri == WeatherLocationResolver.BuildZipLookupUri("75001", "us").ToString()) return StubHttpHandler.NotFound();
            if (uri == WeatherLocationResolver.BuildSearchUri("75001", null).ToString())
                return StubHttpHandler.Ok(WeatherTestData.SamplePostalTie);
            throw new InvalidOperationException($"unexpected leg: {uri}");
        });
        var location = new WeatherLocation("Zip", "75001", null, null, null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        var ambiguous = (WeatherResolutionOutcome.Ambiguous)result;
        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Ambiguous));
        Assert.AreEqual(3, ambiguous.Candidates.Count, "the postal query's pick list is unfiltered");
        Assert.AreEqual(2, handler.Calls, "no hint — the bare search is the only fallback leg");
    }

    [TestMethod]
    public async Task Resolve_PostalFallback_AllLegsEmpty_Unresolved()
    {
        // Nothing anywhere answers (including zippopotam's own non-ZIP
        // response shape, which must degrade the leg, not throw): the verdict
        // is Unresolved — the previous resolution stays valid.
        var (geocoder, handler) = Geocode(_ => StubHttpHandler.Ok("""{ "generationtime_ms": 1.0 }"""));
        var location = new WeatherLocation("Zip", "99998", null, null, null) { CountryCode = "DE" };

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Unresolved));
        Assert.AreEqual(3, handler.Calls, "zippopotam + hinted search + bare search");
    }

    [TestMethod]
    public async Task Resolve_CityFuzzyRows_NotOfferedInPickList()
    {
        // The live "Springfield" response carries fuzzy rows ("Palmyra"):
        // the ranking ignores them (zero score) and the pick list must too —
        // a user who picked a fuzzy row would persist a place the query did
        // not name.
        var (geocoder, handler) = Geocode(_ => StubHttpHandler.Ok(WeatherTestData.SampleSpringfieldsWithFuzzy));
        var location = new WeatherLocation("City", "Springfield, MA", null, null, null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        var resolved = (WeatherResolutionOutcome.Resolved)result;
        Assert.AreEqual(42.10148, resolved.Lat, "the state suffix picks Springfield, Massachusetts");
        Assert.IsNotNull(resolved.RefreshedCandidates);
        Assert.AreEqual(2, resolved.RefreshedCandidates.Count, "the fuzzy 'Palmyra' row must not enter the pick list");
        Assert.AreEqual(1, handler.Calls);
        foreach (var candidate in resolved.RefreshedCandidates)
        {
            Assert.IsTrue(candidate.Label.StartsWith("Springfield", StringComparison.Ordinal), $"fuzzy row offered: {candidate.Label}");
        }
    }

    [TestMethod]
    public async Task Resolve_CityBareWashington_LiveCapitalRowNamedDc_GatesAndExcludesItFromPickList()
    {
        // The live "Washington" search (probed 2026-08): row 1 is the
        // capital, named "Washington D.C." — NOT an exact-name match for
        // "Washington", so it scores zero and never enters the pick list.
        // The state cities named Washington tie at the bare name score, all
        // in one country, with no suffix to match: the same-country
        // tiebreak requires the suffix to have matched the tie, so the gate
        // holds — a bare "Washington" must never population-pick a state
        // city over the capital, and the dropdown offers only the exact-name
        // rows (the escape route to the capital is typing "Washington D.C."
        // or a ZIP — never a silent wrong-city guess).
        const string fixture = """
        {
          "results": [
            { "name": "Washington D.C.", "admin1": "District of Columbia", "country": "United States", "country_code": "US", "population": 689545, "latitude": 38.89511, "longitude": -77.03637 },
            { "name": "Washington", "admin1": "Pennsylvania", "country": "United States", "country_code": "US", "population": 13497, "latitude": 40.17396, "longitude": -80.24617 },
            { "name": "Washington", "admin1": "Indiana", "country": "United States", "country_code": "US", "population": 12078, "latitude": 38.65922, "longitude": -87.17279 },
            { "name": "Washington", "admin1": "North Carolina", "country": "United States", "country_code": "US", "population": 9788, "latitude": 35.54655, "longitude": -77.05217 },
            { "name": "Washington", "admin1": "Iowa", "country": "United States", "country_code": "US", "population": 7408, "latitude": 41.29918, "longitude": -91.69294 }
          ]
        }
        """;
        var (geocoder, handler) = Geocode(_ => StubHttpHandler.Ok(fixture));
        var location = new WeatherLocation("City", "Washington", null, null, null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Ambiguous));
        var ambiguous = (WeatherResolutionOutcome.Ambiguous)result;
        Assert.AreEqual(4, ambiguous.Candidates.Count, "the pick list is the exact-name rows only — the D.C. row (a name the query did not type) is not offered");
        Assert.IsFalse(ambiguous.Candidates.Any(c => c.Label.Contains("D.C.", StringComparison.Ordinal)), "the capital's row must not be pickable under a query that did not name it");
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Resolve_PickMatch_IsCaseInsensitive_ResolvedWithoutHttp()
    {
        // A pick made against the offered candidates resolves directly to
        // that candidate's exact coordinates — no re-geocode.
        var (geocoder, handler) = Geocode(_ => throw new InvalidOperationException("no fetch expected"));
        var location = new WeatherLocation("City", "Berlin", null, null, null) { LocationMatch = "berlin, germany" };

        var result = await geocoder.ResolveAsync(location, [BerlinCandidate], CancellationToken.None);

        var resolved = (WeatherResolutionOutcome.Resolved)result;
        Assert.AreEqual(52.52437, resolved.Lat);
        Assert.AreEqual(13.41053, resolved.Lon);
        Assert.AreEqual("Berlin, Germany", resolved.Label);
        Assert.AreEqual(3426354.0, resolved.Population);
        Assert.AreEqual(0, handler.Calls);
    }

    [TestMethod]
    public async Task Resolve_PickMiss_FallsThroughToCityGeocode()
    {
        var (geocoder, handler) = Geocode(_ => StubHttpHandler.Ok(BerlinBody));
        var location = new WeatherLocation("City", "Berlin", null, null, null) { LocationMatch = "Not, Offered" };

        var result = await geocoder.ResolveAsync(location, [BerlinCandidate], CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Resolved));
        Assert.AreEqual(52.52437, ((WeatherResolutionOutcome.Resolved)result).Lat);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Resolve_PickWithoutCandidates_CityLegStillRuns()
    {
        // A null candidates list must not trip the pick branch — a fresh
        // fetch with no prior city resolution has none to pick from.
        var (geocoder, handler) = Geocode(_ => StubHttpHandler.Ok(BerlinBody));
        var location = new WeatherLocation("City", "Berlin", null, null, null) { LocationMatch = "Berlin, Germany" };

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Resolved));
        Assert.AreEqual(52.52437, ((WeatherResolutionOutcome.Resolved)result).Lat);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Resolve_CityUniqueWinner_ResolvedCarriesDropdownRefresh()
    {
        var (geocoder, handler) = Geocode(_ => StubHttpHandler.Ok(BerlinBody));
        var location = new WeatherLocation("City", "Berlin", null, null, null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        var resolved = (WeatherResolutionOutcome.Resolved)result;
        Assert.AreEqual(52.52437, resolved.Lat);
        Assert.AreEqual(3426354.0, resolved.Population, "the winner's population rides the outcome");
        Assert.IsNotNull(resolved.RefreshedCandidates, "the city leg must refresh the dropdown");
        Assert.AreEqual(1, resolved.RefreshedCandidates.Count);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Resolve_CityTie_AmbiguousCarriesTieCandidates()
    {
        // A bare-name tie must not be guessed — the tie's candidates refresh
        // the dropdown so the user can pick. The pick list carries only the
        // exact-name candidates: the "Brunswick" fuzzy row the live geocoder
        // returns inside a "Berlin" search is NOT a Berlin and must not be
        // offered (it scores zero in the ranking and can never win — but a
        // user who picked it would persist a place the query did not name).
        var (geocoder, handler) = Geocode(_ => StubHttpHandler.Ok(WeatherTestData.SampleBerlines));
        var location = new WeatherLocation("City", "Berlin", null, null, null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Ambiguous));
        Assert.AreEqual(4, ((WeatherResolutionOutcome.Ambiguous)result).Candidates.Count,
            "4 of the 5 live candidates bear the exact name; the fuzzy row is filtered");
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Resolve_CitySearchFailure_Unresolved()
    {
        // A failed geocode is an outcome, not an exception — the caller's
        // previous resolution stays valid.
        var (geocoder, handler) = Geocode(_ => StubHttpHandler.NotFound());
        var location = new WeatherLocation("City", "Berlin", null, null, null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Unresolved));
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task ReadForecast_BuildsForecastUrl_ThroughResolverLeaf()
    {
        // The forecast-URL question is answered by the geocoder's read leg —
        // the client no longer builds any geocoding URL, and the URL comes
        // from the resolver's invariant-formatting leaf.
        var (geocoder, handler) = Geocode(_ => StubHttpHandler.Ok("{}"));

        await geocoder.ReadForecastAsync(40.71, -74.01, CancellationToken.None);

        Assert.AreEqual(WeatherLocationResolver.BuildForecastUri(40.71, -74.01).ToString(), handler.RequestUrls[0]);
        Assert.AreEqual(1, handler.Calls);
    }
}
