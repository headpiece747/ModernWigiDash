using System.IO;
using System.Net;
using System.Net.Http;

namespace ModernWigiDash.Tests;

/// <summary>
/// A stream whose body read never completes: the bounded read's internal
/// deadline (the geocoder's per-instance
/// <see cref="WeatherGeocoder.HttpTimeoutOverride"/>) is what cancels the
/// leg, so the timeout-conversion path is drivable without waiting the real
/// 30 s deadline.
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
/// A stream whose body read faults with an immediate OperationCanceledException
/// (no token involved): the timeout-conversion catch branch runs instantly, so
/// the message is assertable without waiting the effective deadline. The branch
/// is the same one the internal deadline reaches - the production catch
/// converts any non-caller OCE ("the internal deadline OR the shared client's
/// own Timeout").
/// </summary>
internal sealed class ImmediateOceStream : Stream
{
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromException<int>(new OperationCanceledException());

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
    private static WeatherGeocoder Geocoder(HttpMessageHandler handler, List<string>? logs = null, TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler);
        return new(() => client, logs is null ? null : (message, ex) => logs.Add(message), timeout);
    }

    [TestMethod]
    public void HttpTimeoutOverride_IsPerInstance_NotLeakedAcrossGeocoders()
    {
        // The instance-scoped seam: an override set on one geocoder must not
        // bleed into sibling instances (the old static knob was process-wide,
        // so a test run's override silently changed every other geocoder's
        // deadline — and one test's mutation leaked into the next).
        var overridden = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok("{}")), timeout: TimeSpan.FromSeconds(1));
        var plain = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok("{}")));

        Assert.AreEqual(TimeSpan.FromSeconds(1), overridden.HttpTimeoutOverride);
        Assert.IsNull(plain.HttpTimeoutOverride, "the override must not leak to sibling instances");
    }

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
    public async Task GeocodeZipAsync_MalformedLatitude_FollowsUnusableCoordinatesPath()
    {
        // The tolerant getters apply to the ZIP leg: a response with a missing,
        // null, or NON-STRING (numeric) latitude all read as unusable
        // coordinates - the same "unusable coordinates" failure path, not a raw
        // exception into the caller's catch - so the leg returns null and the
        // caller (WeatherClient) falls back to the city geocoder. This test
        // pins the LEG's failure shape only; the fallback itself lives in
        // WeatherClient, not here.
        var logs = new List<string>();
        var missing = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok("""{"places":[{"longitude":"-97.7431","state":"Texas"}]}""")), logs);
        var nullLat = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok("""{"places":[{"latitude":null,"longitude":"-97.7431","state":"Texas"}]}""")));
        var numericLat = Geocoder(new StubHttpHandler(_ => StubHttpHandler.Ok("""{"places":[{"latitude":30.2672,"longitude":"-97.7431","state":"Texas"}]}""")));

        Assert.IsNull(await missing.GeocodeZipAsync("78701", "us", CancellationToken.None),
            "a missing-latitude ZIP leg must return null (the caller falls back to the city geocoder)");
        Assert.IsNull(await nullLat.GeocodeZipAsync("78701", "us", CancellationToken.None),
            "a null-latitude ZIP leg must return null (the caller falls back to the city geocoder)");
        Assert.IsNull(await numericLat.GeocodeZipAsync("78701", "us", CancellationToken.None),
            "a non-string latitude must read as unusable coordinates, not throw a raw exception");
        Assert.IsTrue(logs.Any(l => l.Contains("unusable coordinates", StringComparison.Ordinal)),
            "the malformed-coordinate failure must surface through the error sink");
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
    public async Task GeocodeCityAsync_Timeout_IsConvertedToUnresolvedAndLogged()
    {
        // The internal body-read deadline must NOT surface as a plain
        // cancellation: an OCE would skip the failure path (log + fallback
        // shape) and leave a silent retry loop. The deadline converts to a
        // TimeoutException, which the catch turns into the Unresolved shape
        // plus an error-sink line.
        var logs = new List<string>();
        var geocoder = Geocoder(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream()) }),
            logs, TimeSpan.FromMilliseconds(50));

        var result = await geocoder.GeocodeCityAsync("Berlin", null, null, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(WeatherCityGeocodeResult.Unresolved),
            "the deadline must convert to the failure shape, not swallow as OCE");
        Assert.IsTrue(logs.Any(l => l.Contains("Geocoding failed", StringComparison.Ordinal)),
            "the timeout must surface through the error sink");
    }

    [TestMethod]
    public async Task GeocodeCityAsync_CallerCancellation_PropagatesNotConvertedToTimeout()
    {
        // The converter distinguishes the caller's token from the internal
        // deadline: a pre-cancelled caller token must propagate as the
        // cancellation (TaskCanceledException), never be converted to a
        // TimeoutException and swallowed into the Unresolved shape.
        var geocoder = Geocoder(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream()) }),
            timeout: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => geocoder.GeocodeCityAsync("Berlin", null, null, cts.Token));
    }

    [TestMethod]
    public async Task ReadBoundedAsync_Timeout_MessageStatesOverrideDeadline()
    {
        // The timeout-conversion message states the EFFECTIVE deadline: with
        // the test-seam override set, the message reflects it (1s), never the
        // 30 s default - a stale default in the message would mislead the log
        // reader about how long the leg actually waited.
        var geocoder = Geocoder(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PendingStream()) }),
            timeout: TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => geocoder.ReadBoundedAsync("https://api.open-meteo.com/v1/search?name=Berlin", CancellationToken.None));

        Assert.AreEqual("HTTP leg exceeded the 1s deadline", ex.Message);
    }

    [TestMethod]
    public async Task ReadBoundedAsync_Timeout_MessageStatesDefaultDeadline()
    {
        // Without an override, the message states the 30 s DEFAULT deadline.
        // The stream faults with an immediate OCE of its own (no token
        // involved), so the converter's catch branch - the same one the
        // internal deadline and the shared client's own timeout reach - runs
        // without waiting the real deadline; the message is built from the
        // same (HttpTimeoutOverride ?? HttpTimeout) expression.
        var geocoder = Geocoder(new StubHttpHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ImmediateOceStream()) }));

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => geocoder.ReadBoundedAsync("https://api.open-meteo.com/v1/search?name=Berlin", CancellationToken.None));

        Assert.AreEqual("HTTP leg exceeded the 30s deadline", ex.Message);
    }
}
