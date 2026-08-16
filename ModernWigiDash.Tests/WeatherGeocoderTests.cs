using System.IO;
using System.Net;
using System.Net.Http;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// A stream whose body read never completes: the bounded read's internal
/// deadline (<see cref="WeatherGeocoder.HttpTimeoutOverride"/>) is what
/// cancels the leg, so the timeout-conversion path is drivable without
/// waiting the real 30 s deadline.
/// </summary>
internal sealed class PendingStream : Stream
{
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// The geocoding adapter behind <see cref="WeatherClient"/>: the three
/// endpoint legs (city search, city geocode, ZIP lookup), the tolerant
/// candidate parsing, and the coordinate helpers. The resolver's DECISION
/// rules are pinned by WeatherLocationResolverTests; this file pins the
/// HTTP + parse adapter.
/// </summary>
[TestClass]
public class WeatherGeocoderTests
{
    private static WeatherGeocoder Geocoder(HttpMessageHandler handler, List<string>? logs = null)
        => new(new HttpClient(handler), logs is null ? null : (message, ex) => logs.Add(message));

    [TestMethod]
    public async Task SearchCitiesAsync_MalformedCandidateRow_IsSkippedNotFatal()
    {
        // One candidate with a missing lat must not abort the parse of the
        // whole response — the valid rows still come back.
        const string body = """{"results":[{"name":"Berlin","latitude":52.52,"longitude":13.405,"country":"Germany"},{"name":"Broken","country":"Nowhere"}]}""";
        var geocoder = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok(body)));

        var results = await geocoder.SearchCitiesAsync("Berlin", CancellationToken.None);

        Assert.AreEqual(1, results.Count, "the malformed row must degrade only itself");
        Assert.AreEqual("Berlin, Germany", results[0].Label);
    }

    [TestMethod]
    public async Task GeocodeCityAsync_AllCandidatesMalformed_ReturnsUnresolved()
    {
        const string body = """{"results":[{"name":"Broken","country":"Nowhere"}]}""";
        var geocoder = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok(body)));

        var result = await geocoder.GeocodeCityAsync("Broken", null, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherCityGeocodeResult.Unresolved));
    }

    [TestMethod]
    public async Task GeocodeCityAsync_ResponseWithoutResults_ReturnsUnresolved()
    {
        var geocoder = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok("""{"results":[]}""")));

        var result = await geocoder.GeocodeCityAsync("Nowhere", null, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherCityGeocodeResult.Unresolved));
    }

    [TestMethod]
    public async Task GeocodeCityAsync_NonStringField_IsTolerated()
    {
        // A numeric admin1 field must read as "" — not throw the whole parse.
        const string body = """{"results":[{"name":"Odd","latitude":1.0,"longitude":2.0,"admin1":123,"country":"X"}]}""";
        var geocoder = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok(body)));

        var result = await geocoder.GeocodeCityAsync("Odd", null, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherCityGeocodeResult.Resolved));
    }

    [TestMethod]
    public async Task GeocodeZipAsync_MissingPlaceName_ComposesStateOnly()
    {
        // zippopotam may omit "place name" — the label must not become ", Texas".
        const string body = """{"places":[{"latitude":"30.2672","longitude":"-97.7431","state":"Texas"}]}""";
        var geocoder = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok(body)));

        var result = await geocoder.GeocodeZipAsync("78701", "us", CancellationToken.None);

        Assert.IsNotNull(result);
        Assert.AreEqual("Texas", result.CityName);
    }

    [TestMethod]
    public async Task GeocodeZipAsync_HttpFailure_ReturnsNullForCityFallback()
    {
        var logs = new List<string>();
        var geocoder = Geocoder(new StubHttpHandler(_ => StubHttpHandler.NotFound()), logs);

        var result = await geocoder.GeocodeZipAsync("78701", "us", CancellationToken.None);

        Assert.IsNull(result, "a failed ZIP leg must fall back to the city geocoder");
        Assert.IsTrue(logs.Count > 0, "the failure must surface through the error sink");
    }

    [TestMethod]
    public async Task GeocodeCityAsync_OversizedBody_IsBoundedAndReturnsUnresolved()
    {
        // A multi-megabyte response must be truncated at the bounded-read cap
        // instead of being buffered unboundedly. The payload is VALID JSON
        // padded past the cap: without the bounded read the parse would
        // succeed and resolve — so a regression in the cap fails this test
        // instead of slipping through on invalid JSON.
        string padded = """{"results":[{"name":"Berlin","latitude":52.52,"longitude":13.405,"country":"Germany"}]}"""
            + new string(' ', 3 * 1024 * 1024);
        var geocoder = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok(padded)));

        var result = await geocoder.GeocodeCityAsync("Berlin", null, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherCityGeocodeResult.Unresolved));
    }

    [TestMethod]
    public void TryParseCoordinatePair_ValidPair_Parses()
    {
        Assert.IsTrue(WeatherGeocoder.TryParseCoordinatePair("52.52, 13.405", out double lat, out double lon));
        Assert.AreEqual(52.52, lat, 0.0001);
        Assert.AreEqual(13.405, lon, 0.0001);
    }

    [TestMethod]
    public void TryParseCoordinatePair_NotAPair_ReturnsFalse()
    {
        Assert.IsFalse(WeatherGeocoder.TryParseCoordinatePair("52.52", out _, out _));
        Assert.IsFalse(WeatherGeocoder.TryParseCoordinatePair("Berlin", out _, out _));
    }

    [TestMethod]
    public void TryParseCoordinatePair_NonFiniteOrOutOfRange_ReturnsFalse()
    {
        // "NaN" and "Infinity" PARSE as valid doubles — the coordinate
        // validation is what rejects them, along with the range bounds.
        Assert.IsFalse(WeatherGeocoder.TryParseCoordinatePair("NaN, 5", out _, out _));
        Assert.IsFalse(WeatherGeocoder.TryParseCoordinatePair("5, NaN", out _, out _));
        Assert.IsFalse(WeatherGeocoder.TryParseCoordinatePair("Infinity, 5", out _, out _));
        Assert.IsFalse(WeatherGeocoder.TryParseCoordinatePair("-Infinity, 5", out _, out _));
        Assert.IsFalse(WeatherGeocoder.TryParseCoordinatePair("91, 0", out _, out _), "|lat| > 90 is out of range");
        Assert.IsFalse(WeatherGeocoder.TryParseCoordinatePair("-91, 0", out _, out _));
        Assert.IsFalse(WeatherGeocoder.TryParseCoordinatePair("45, 181", out _, out _), "|lon| > 180 is out of range");
        Assert.IsFalse(WeatherGeocoder.TryParseCoordinatePair("45, -181", out _, out _));
        // Boundary values are still valid.
        Assert.IsTrue(WeatherGeocoder.TryParseCoordinatePair("90, 180", out _, out _));
    }

    [TestMethod]
    public void FormatCoordinates_InvariantTwoDecimals()
    {
        Assert.AreEqual("52.52, 13.41", WeatherGeocoder.FormatCoordinates(52.52, 13.406));
    }

    [TestMethod]
    public async Task GeocodeCityAsync_Timeout_IsConvertedToUnresolvedAndLogged()
    {
        // The internal body-read deadline must NOT surface as a plain
        // cancellation: an OCE would skip the failure path (log + fallback
        // shape) and leave a silent retry loop. The deadline converts to a
        // TimeoutException, which the catch turns into the Unresolved shape
        // plus an error-sink line.
        var original = WeatherGeocoder.HttpTimeoutOverride;
        WeatherGeocoder.HttpTimeoutOverride = TimeSpan.FromMilliseconds(50);
        try
        {
            var logs = new List<string>();
            var geocoder = Geocoder(new StubHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream()) }), logs);

            var result = await geocoder.GeocodeCityAsync("Berlin", null, null, CancellationToken.None);

            Assert.IsInstanceOfType(result, typeof(WeatherCityGeocodeResult.Unresolved),
                "the deadline must convert to the failure shape, not swallow as OCE");
            Assert.IsTrue(logs.Any(l => l.Contains("Geocoding failed", StringComparison.Ordinal)),
                "the timeout must surface through the error sink");
        }
        finally
        {
            WeatherGeocoder.HttpTimeoutOverride = original;
        }
    }

    [TestMethod]
    public async Task GeocodeCityAsync_CallerCancellation_PropagatesNotConvertedToTimeout()
    {
        // The converter distinguishes the caller's token from the internal
        // deadline: a pre-cancelled caller token must propagate as the
        // cancellation (TaskCanceledException), never be converted to a
        // TimeoutException and swallowed into the Unresolved shape.
        var original = WeatherGeocoder.HttpTimeoutOverride;
        WeatherGeocoder.HttpTimeoutOverride = TimeSpan.FromMilliseconds(50);
        try
        {
            var geocoder = Geocoder(new StubHttpHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream()) }));
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            await Assert.ThrowsAsync<TaskCanceledException>(
                () => geocoder.GeocodeCityAsync("Berlin", null, null, cts.Token));
        }
        finally
        {
            WeatherGeocoder.HttpTimeoutOverride = original;
        }
    }
}
