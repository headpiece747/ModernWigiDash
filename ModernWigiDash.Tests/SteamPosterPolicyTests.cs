using System.Diagnostics;
using System.Net.Http;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

/// <summary>
/// The poster policy's visible-set gate: a steady tick (the same games-list
/// reference + the same visible AppId sequence) re-requests nothing, while a changed
/// visible set (a new games list, a scrolled sequence) re-requests the
/// on-demand fetches. The gate is the per-tick no-op the render loop needs;
/// the fetches are observed through the CDN leg (the local-file path is
/// absent, so every request is one CDN GET).
/// </summary>
[TestClass]
public sealed class SteamPosterPolicyTests
{
    /// <summary>A library source with no installation (no local poster file: every request is a CDN GET).</summary>
    private sealed class NoSteamSource : ISteamLibrarySource
    {
        public string? SteamPath => null;

        public IReadOnlyList<SteamAppManifest> LoadManifests(string? steamPath) => [];
    }

    /// <summary>A CDN handler that counts the GETs it serves (a 404 answer, so nothing is cached).</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly System.Threading.Lock _gate = new();
        private int _count;

        public int Count
        {
            get { lock (_gate) return _count; }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_gate)
                _count++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    private static SteamGameItem Game(uint appId)
        => new(appId, "Game", "dir", SteamGameState.Idle, 0f, 0, null, false);

    /// <summary>
    /// The fire-and-forget fetches are async: a bounded wait lets them drain
    /// before a count is read. The settle after reaching the count lets the
    /// in-flight set clear (the fetch's <c>ContinueWith</c> removes the app),
    /// so a following call can re-request.
    /// </summary>
    private static void Drain(CountingHandler handler, int expectedCount)
    {
        var elapsed = Stopwatch.StartNew();
        while (handler.Count < expectedCount && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            Thread.Sleep(10);

        Thread.Sleep(100);
    }

    private static SteamPosterPolicy MakePolicy(CountingHandler handler)
        => new(new NoSteamSource(), new SteamPosterCache(), new SteamWebApiClient(TimeProvider.System, new HttpClient(handler)), _ => { });

    [TestMethod]
    public void SteadyState_StableVisibleSet_RequestsNothingOnTheSecondTick()
    {
        var handler = new CountingHandler();
        using var policy = MakePolicy(handler);

        IReadOnlyList<SteamGameItem> games = [Game(100), Game(200)];

        policy.RequestPosters(games, [100, 200]);
        Drain(handler, 2);
        Assert.AreEqual(2, handler.Count, "the first tick requests the two uncached visible posters");

        policy.RequestPosters(games, [100, 200]);
        Drain(handler, 2);
        Assert.AreEqual(2, handler.Count, "a steady tick (the same games list + visible sequence) re-requests nothing");
    }

    [TestMethod]
    public void ChangedGamesList_ReRequestsTheVisibleSet()
    {
        var handler = new CountingHandler();
        using var policy = MakePolicy(handler);

        IReadOnlyList<SteamGameItem> first = [Game(100), Game(200)];
        IReadOnlyList<SteamGameItem> second = [Game(100), Game(200)];

        policy.RequestPosters(first, [100, 200]);
        Drain(handler, 2);
        Assert.AreEqual(2, handler.Count);

        policy.RequestPosters(second, [100, 200]);
        Drain(handler, 4);
        Assert.AreEqual(4, handler.Count, "a new games-list reference is a new visible set: the fetches re-run");
    }

    [TestMethod]
    public void ChangedVisibleIds_ReRequestsTheVisibleSet()
    {
        var handler = new CountingHandler();
        using var policy = MakePolicy(handler);

        IReadOnlyList<SteamGameItem> games = [Game(100), Game(200)];

        policy.RequestPosters(games, [100, 200]);
        Drain(handler, 2);
        Assert.AreEqual(2, handler.Count);

        policy.RequestPosters(games, [200, 100]);
        Drain(handler, 4);
        Assert.AreEqual(4, handler.Count, "a changed visible id sequence is a new visible set: the fetches re-run");
    }
}
