using System.IO;
using System.Net.Http;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the weather client's fetch-side atomic transitions: the single-flight
/// claim, the throttle window, the identity guard (Stamp/ConfirmAndStamp), and
/// the cache-identity apply. Driven through the client's public surface only —
/// no direct access to private fields or internal resolution state.
/// </summary>
[TestClass]
public class WeatherClientFetchStateTests
{
    private static readonly string TempRoot = Path.Combine(Path.GetTempPath(), "wmd-weather-client-fetch-state-tests");

    [ClassCleanup]
    public static void Cleanup()
    {
        try { Directory.Delete(TempRoot, recursive: true); } catch { /* best-effort */ }
    }

    private static string NewTempDir() => Path.Combine(TempRoot, Guid.NewGuid().ToString("N"));

    private static WeatherClient CreateClient(FakeTimeProvider clock)
        => new(NewTempDir(), "weather_test.json", timeProvider: clock);

    private static WeatherLocation CoordinateLocation => new("Fixed Location", "40.71,-74.00", null, null, null);

    // ── Begin / single-flight claim / throttle ──────────────────────────────

    [TestMethod]
    public async Task FetchCurrentAsync_FreshClient_ReturnsFetchedAndStampsThrottle()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleGeocode);
            if (url.Contains("/v1/forecast", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleForecast);
            return StubHttpHandler.NotFound();
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var client = CreateClient(clock);
        client.TestHttpClient = new HttpClient(stub);

        var result = await client.FetchCurrentAsync(CoordinateLocation);

        Assert.IsInstanceOfType(result, typeof(WeatherFetchResult.Fetched), "the first attempt must succeed");
        Assert.IsTrue(client.HasFetched, "the success stamped the throttle");
        Assert.IsFalse(client.IsFetchWindowElapsed(), "the stamp primed the 5-minute window");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_SecondCallWithinWindow_ReturnsThrottled()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleGeocode);
            if (url.Contains("/v1/forecast", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleForecast);
            return StubHttpHandler.NotFound();
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var client = CreateClient(clock);
        client.TestHttpClient = new HttpClient(stub);

        _ = await client.FetchCurrentAsync(CoordinateLocation);
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = await client.FetchCurrentAsync(CoordinateLocation);

        Assert.IsInstanceOfType(second, typeof(WeatherFetchResult.Throttled), "inside the 5-minute window the attempt cools down");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_AfterWindowElapsed_ReturnsFetched()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleGeocode);
            if (url.Contains("/v1/forecast", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleForecast);
            return StubHttpHandler.NotFound();
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var client = CreateClient(clock);
        client.TestHttpClient = new HttpClient(stub);

        _ = await client.FetchCurrentAsync(CoordinateLocation);
        clock.Advance(TimeSpan.FromMinutes(5));
        var next = await client.FetchCurrentAsync(CoordinateLocation);

        Assert.IsInstanceOfType(next, typeof(WeatherFetchResult.Fetched), "exactly 5 minutes elapses the window (>= comparison)");
    }

    [TestMethod]
    public async Task FetchCurrentAsync_ForcedBypassesThrottle()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleGeocode);
            if (url.Contains("/v1/forecast", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleForecast);
            return StubHttpHandler.NotFound();
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var client = CreateClient(clock);
        client.TestHttpClient = new HttpClient(stub);

        _ = await client.FetchCurrentAsync(CoordinateLocation);
        clock.Advance(TimeSpan.FromMinutes(1));
        var forced = await client.FetchCurrentAsync(CoordinateLocation, force: true);

        Assert.IsInstanceOfType(forced, typeof(WeatherFetchResult.Fetched), "a forced attempt (explicit user refresh) bypasses the window");
    }

    // ── Invalidate resets the throttle ──────────────────────────────────────

    [TestMethod]
    public async Task Invalidate_Location_ResetsThrottleSoNextFetchRunsImmediately()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleGeocode);
            if (url.Contains("/v1/forecast", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherTestData.SampleForecast);
            return StubHttpHandler.NotFound();
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var client = CreateClient(clock);
        client.TestHttpClient = new HttpClient(stub);

        _ = await client.FetchCurrentAsync(CoordinateLocation);
        clock.Advance(TimeSpan.FromMinutes(1));
        client.Invalidate(WeatherInvalidationKind.Location);

        Assert.IsTrue(client.IsFetchWindowElapsed(), "the invalidation reset the throttle so the next fetch runs immediately");
        Assert.AreEqual("", client.Identity.ResolvedName, "the whole identity voided");
    }
}
