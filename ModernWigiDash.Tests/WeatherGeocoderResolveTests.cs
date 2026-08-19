using System.Net.Http;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The geocoder's single resolution entry: the one place the client asks
/// "where is this" — the whole resolution ladder (explicit coordinates, a
/// "lat,lon" pair, ZIP via zippopotam, a dropdown pick, the city-name
/// geocode) spelled once behind the door, plus the forecast-URL read leg
/// that owns the one geocoding URL the client no longer builds. The
/// resolver's DECISION rules stay pinned in WeatherLocationResolverTests;
/// this file pins the door's routing, the outcome shapes, and the HTTP
/// leg(s) each leg performs.
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
        return (new WeatherGeocoder(new HttpClient(handler)), handler);
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
        // The zippopotam leg 404s (US-only service) — the door must fall back
        // to the worldwide city geocode WITH the original location so the
        // country hint is carried.
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
        // the dropdown so the user can pick.
        var (geocoder, handler) = Geocode(_ => StubHttpHandler.Ok(WeatherTestData.SampleBerlines));
        var location = new WeatherLocation("City", "Berlin", null, null, null);

        var result = await geocoder.ResolveAsync(location, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherResolutionOutcome.Ambiguous));
        Assert.AreEqual(5, ((WeatherResolutionOutcome.Ambiguous)result).Candidates.Count);
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