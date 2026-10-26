using System.Diagnostics;
using System.IO;
using System.Net.Http;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Steam library service's persona wiring: the optional community-API
/// persona (avatar + name + online state) is folded into the published
/// snapshot, and a blank credential pair clears it. The persona is injected
/// through the test seam, so no HTTP is touched.
/// </summary>
[TestClass]
public sealed class SteamLibraryServiceTests
{
    /// <summary>A source with no installation and no manifests (no disk warmup).</summary>
    private sealed class EmptySource : ISteamLibrarySource
    {
        public string? SteamPath => null;
        public IReadOnlyList<SteamAppManifest> LoadManifests(string? steamPath) => [];
    }

    /// <summary>A source whose games list grows one app per call (a clobber is a size regression).</summary>
    private sealed class GrowingSource : ISteamLibrarySource
    {
        private int _call;

        public string? SteamPath => null;

        public IReadOnlyList<SteamAppManifest> LoadManifests(string? steamPath)
        {
            int count = ++_call;
            var manifests = new List<SteamAppManifest>(count);
            for (int i = 0; i < count; i++)
                manifests.Add(new SteamAppManifest((uint)(1000 + i), "Game", "dir", "g.exe", SteamAppState.FullyInstalled, 0, 0, 0, 1.0));
            return manifests;
        }
    }

    /// <summary>A process bridge that reports nothing running and closes cleanly.</summary>
    private sealed class NoOpBridge : ISteamProcessBridge
    {
        public bool IsRunning(string processName) => false;
        public bool TryClose(string processName, TimeSpan budget) => true;
    }

    /// <summary>A source over a temp Steam dir (a real local poster for one app), for the local-load leg.</summary>
    private sealed class TempSource(string steamPath) : ISteamLibrarySource
    {
        public string? SteamPath => steamPath;
        public IReadOnlyList<SteamAppManifest> LoadManifests(string? steamPath)
            => [new SteamAppManifest(730u, "Game", "gamedir", "game.exe", SteamAppState.FullyInstalled, 0, 0, 0, 10.0)];
    }

    /// <summary>A service over inert seams, capturing every published snapshot.</summary>
    private static (SteamLibraryService Service, List<SteamLibrarySnapshot> Published) Create()
    {
        List<SteamLibrarySnapshot> published = [];
        var source = new EmptySource();
        var api = new SteamWebApiClient();
        var service = new SteamLibraryService(
            source,
            new SteamProcessTracker(new NoOpBridge(), _ => { }),
            api,
            published.Add,
            _ => { });
        return (service, published);
    }

    [TestMethod]
    public void SetPersonaForTest_PublishesASnapshotCarryingThePersona()
    {
        (SteamLibraryService service, List<SteamLibrarySnapshot> published) = Create();
        var persona = new SteamPersona("https://example.com/a.jpg", "Tester", 2);
        using var avatar = new SKBitmap(8, 8);

        service.SetPersonaForTest(persona, avatar);

        Assert.AreEqual(persona, service.Current.Persona);
        Assert.AreSame(avatar, service.BeginRenderFrame().Avatar);
        Assert.AreEqual(persona, published[^1].Persona);
    }

    [TestMethod]
    public void SetPersonaForTest_Null_ClearsThePersonaAndAvatar()
    {
        (SteamLibraryService service, _) = Create();
        var persona = new SteamPersona("https://example.com/a.jpg", "Name", 1);
        using var avatar = new SKBitmap(8, 8);
        service.SetPersonaForTest(persona, avatar);
        Assert.IsNotNull(service.Current.Persona);

        service.SetPersonaForTest(null, null);

        Assert.IsNull(service.Current.Persona);
        Assert.IsNull(service.BeginRenderFrame().Avatar);
    }

    [TestMethod]
    public void SetCredentials_BlankPair_ClearsThePersona()
    {
        (SteamLibraryService service, _) = Create();
        var persona = new SteamPersona("https://example.com/a.jpg", "Name", 1);
        using var avatar = new SKBitmap(8, 8);
        service.SetPersonaForTest(persona, avatar);
        Assert.IsNotNull(service.Current.Persona);

        service.SetCredentials("", "");

        Assert.IsNull(service.Current.Persona);
        Assert.IsNull(service.BeginRenderFrame().Avatar);
    }

    [TestMethod]
    public void PersonaFold_OverlaysTheLatestSnapshot_NotAStaleTick()
    {
        var source = new GrowingSource();
        var api = new SteamWebApiClient();
        using var service = new SteamLibraryService(
            source,
            new SteamProcessTracker(new NoOpBridge(), _ => { }),
            api,
            _ => { },
            _ => { });
        var persona = new SteamPersona("https://example.com/a.jpg", "Tester", 2);
        using var avatar = new SKBitmap(8, 8);

        service.RefreshNow();
        service.RefreshNow();
        service.SetPersonaForTest(persona, avatar);

        Assert.AreEqual(2, service.Current.Games.Count, "the persona fold overlays the LATEST tick, not a stale one");
        Assert.AreEqual(persona, service.Current.Persona);
    }

    [TestMethod]
    public void TickAndPersonaFold_NeverClobberThePublishedGamesList()
    {
        var source = new GrowingSource();
        var api = new SteamWebApiClient();
        var sizes = new List<int>();
        var sizesGate = new System.Threading.Lock();
        using var service = new SteamLibraryService(
            source,
            new SteamProcessTracker(new NoOpBridge(), _ => { }),
            api,
            snapshot =>
            {
                lock (sizesGate)
                    sizes.Add(snapshot.Games.Count);
            },
            _ => { });
        var persona = new SteamPersona("https://example.com/a.jpg", "Tester", 2);
        using var avatar = new SKBitmap(8, 8);

        const int rounds = 4000;
        var ticker = Task.Run(() => { for (int i = 0; i < rounds; i++) service.RefreshNow(); });
        var folder = Task.Run(() => { for (int i = 0; i < rounds; i++) service.SetPersonaForTest(persona, avatar); });
        Task.WaitAll(ticker, folder);

        lock (sizesGate)
        {
            int previous = 0;
            foreach (int size in sizes)
            {
                Assert.IsTrue(size >= previous, "a stale tick must never be re-published over a newer one (the single-writer invariant)");
                previous = size;
            }
        }
    }

    [TestMethod]
    public void LoadLocalPosterForTest_PopulatesTheCacheFromTheLocalFile()
    {
        string temp = Path.Combine(Path.GetTempPath(), "wmd-steam-poster-" + Guid.NewGuid().ToString("N"));
        string posterDir = Path.Combine(temp, "appcache", "librarycache", "730");
        Directory.CreateDirectory(posterDir);
        File.WriteAllBytes(Path.Combine(posterDir, SteamPosterSource.PosterFileName), MakeJpegBytes());

        var source = new TempSource(temp);
        var cache = new SteamPosterCache();
        var policy = new SteamPosterPolicy(source, cache, new SteamWebApiClient(), _ => { });

        try
        {
            Assert.IsFalse(cache.TryGet(730u, out _), "no poster before the load");

            policy.LoadLocalPosterForTest(730u);

            Assert.IsTrue(cache.TryGet(730u, out _), "the local poster must land in the cache");
        }
        finally
        {
            policy.Dispose();
            Directory.Delete(temp, true);
        }
    }

    /// <summary>A process bridge that records every close and reports nothing running.</summary>
    private sealed class RecordingBridge : ISteamProcessBridge
    {
        public List<string> Closed { get; } = [];

        public bool IsRunning(string processName) => false;

        public bool TryClose(string processName, TimeSpan budget)
        {
            Closed.Add(processName);
            return true;
        }
    }

    /// <summary>A source over one installed manifest (the process-name map fixture).</summary>
    private sealed class OneGameSource : ISteamLibrarySource
    {
        public string? SteamPath => null;

        public IReadOnlyList<SteamAppManifest> LoadManifests(string? steamPath)
            => [new SteamAppManifest(730u, "Game", "gamedir", "game.exe", SteamAppState.FullyInstalled, 0, 0, 0, 10.0)];
    }

    [TestMethod]
    public async Task Terminate_ClosesTheGamesProcessTree()
    {
        var bridge = new RecordingBridge();
        var source = new OneGameSource();
        var api = new SteamWebApiClient();
        using var service = new SteamLibraryService(
            source,
            new SteamProcessTracker(bridge, _ => { }),
            api,
            _ => { },
            _ => { });
        service.RefreshNow();

        await service.Terminate(730u);

        CollectionAssert.AreEqual(new[] { "game" }, bridge.Closed, "the app id must resolve to its process name and close that tree");
    }

    [TestMethod]
    public async Task Terminate_UnknownApp_DoesNotTouchAnyProcess()
    {
        var bridge = new RecordingBridge();
        var source = new OneGameSource();
        var api = new SteamWebApiClient();
        using var service = new SteamLibraryService(
            source,
            new SteamProcessTracker(bridge, _ => { }),
            api,
            _ => { },
            _ => { });
        service.RefreshNow();

        await service.Terminate(999u);

        Assert.IsEmpty(bridge.Closed, "an app id with no process name must not close anything");
    }

    /// <summary>One service over a recording bridge + launch-URI sink (the tap decision's two legs).</summary>
    private static (SteamLibraryService Service, RecordingBridge Bridge, List<string> LaunchUris) MakeActivateService()
    {
        var bridge = new RecordingBridge();
        var launchUris = new List<string>();
        var source = new OneGameSource();
        var api = new SteamWebApiClient();
        var service = new SteamLibraryService(source, new SteamProcessTracker(bridge, launchUris.Add), api, _ => { }, _ => { });
        service.RefreshNow();
        return (service, bridge, launchUris);
    }

    private static SteamGameItem Game(SteamGameState state)
        => new(730u, "Game", "dir", state, 0f, 0, null, false);

    [TestMethod]
    public async Task Activate_RunningGame_TerminatesIt()
    {
        (SteamLibraryService service, RecordingBridge bridge, List<string> launchUris) = MakeActivateService();

        service.Activate(Game(SteamGameState.Running));

        // The close is fire-and-forget (the off-render contract): a bounded wait lets it land.
        var elapsed = Stopwatch.StartNew();
        while (bridge.Closed.Count == 0 && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(10).ConfigureAwait(false);

        CollectionAssert.AreEqual(new[] { "game" }, bridge.Closed, "a running game is terminated (the close contract)");
        Assert.AreEqual(0, launchUris.Count, "a running game is not launched");
    }

    [TestMethod]
    public void Activate_LaunchableGame_LaunchesIt()
    {
        (SteamLibraryService service, RecordingBridge bridge, List<string> launchUris) = MakeActivateService();

        service.Activate(Game(SteamGameState.Idle));

        Assert.AreEqual(1, launchUris.Count, "an idle (launchable) game launches");
        Assert.IsTrue(launchUris[0].Contains("730"), "the launch URI carries the app id");
        Assert.AreEqual(0, bridge.Closed.Count, "an idle game is not terminated");
    }

    [TestMethod]
    public void Activate_UnlaunchableGame_DoesNothing()
    {
        (SteamLibraryService service, RecordingBridge bridge, List<string> launchUris) = MakeActivateService();

        service.Activate(Game(SteamGameState.Uninstalled));

        Assert.AreEqual(0, bridge.Closed.Count, "an uninstalled game is not terminated");
        Assert.AreEqual(0, launchUris.Count, "an uninstalled game is not launched");
    }

    [TestMethod]
    public void ToggleFavorite_RoundTripsThroughTheCanonicalJson()
    {
        var source = new OneGameSource();
        var api = new SteamWebApiClient();
        using var service = new SteamLibraryService(
            source,
            new SteamProcessTracker(new RecordingBridge(), _ => { }),
            api,
            _ => { },
            _ => { });

        string json = service.ToggleFavorite(730u);
        Assert.AreEqual("[730]", json, "one favorite is the canonical single-id array");

        service.SetFavoritesJson(json);
        json = service.ToggleFavorite(730u);
        Assert.AreEqual("[]", json, "toggling off must return the empty canonical array");
    }

    [TestMethod]
    public void ToggleFavorite_TwoTogglesRestoreTheOriginalState()
    {
        var source = new OneGameSource();
        var api = new SteamWebApiClient();
        using var service = new SteamLibraryService(
            source,
            new SteamProcessTracker(new RecordingBridge(), _ => { }),
            api,
            _ => { },
            _ => { });

        string on = service.ToggleFavorite(730u);
        service.ToggleFavorite(730u);
        string again = service.ToggleFavorite(730u);
        Assert.AreEqual(on, again, "two toggles are the identity: the set, not the string, owns the state");
    }

    [TestMethod]
    public void ToggleFavorite_RefreshesThePublishedStar_WithoutWaitingATick()
    {
        var source = new OneGameSource();
        var api = new SteamWebApiClient();
        using var service = new SteamLibraryService(
            source,
            new SteamProcessTracker(new RecordingBridge(), _ => { }),
            api,
            _ => { },
            _ => { });

        service.RefreshNow();
        Assert.IsFalse(service.Current.Games.Single(g => g.AppId == 730u).IsFavorite, "a fresh library has no favorites");

        string json = service.ToggleFavorite(730u);

        Assert.IsTrue(service.Current.Games.Single(g => g.AppId == 730u).IsFavorite, "the toggle refreshes the published snapshot's star immediately (no 3s tick)");

        // The widget's push-back adopts the service's own canonical string (no re-derive to empty).
        service.SetFavoritesJson(json);
        Assert.IsTrue(service.Current.Games.Single(g => g.AppId == 730u).IsFavorite, "the push-back adopts the canonical string, it does not re-derive to empty");

        service.ToggleFavorite(730u);
        Assert.IsFalse(service.Current.Games.Single(g => g.AppId == 730u).IsFavorite, "a second toggle un-favorites");
    }

    /// <summary>A stub poster CDN: records every requested URL and serves a valid poster.</summary>
    private sealed class RecordingPosterHandler : HttpMessageHandler
    {
        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(MakeJpegBytes()),
            };
            return Task.FromResult(response);
        }
    }

    [TestMethod]
    public async Task RequestShowcasePosters_RequestsOnlyTheMissingCards()
    {
        var handler = new RecordingPosterHandler();
        var http = new HttpClient(handler);
        var source = new OneGameSource();
        var api = new SteamWebApiClient(TimeProvider.System, http);
        using var service = new SteamLibraryService(
            source,
            new SteamProcessTracker(new RecordingBridge(), _ => { }),
            api,
            _ => { },
            _ => { });

        try
        {
            // The render facade: the poster request is the service's name; the fetch rule stays the policy's (the test drives the cache through its concrete test seam).
            var cache = service.PosterCacheForTest;
            // The focal card is already cached: only the missing flank may be requested.
            cache.InsertForTest(100u, new SKBitmap(8, 8, SKColorType.Rgb565, SKAlphaType.Opaque));
            var focus = new SteamGameItem(100u, "Focus", "dir", SteamGameState.Idle, 1f, 10.0, null, false);
            var previous = new SteamGameItem(99u, "Prev", "dir", SteamGameState.Idle, 1f, 10.0, null, false);

            service.RequestShowcasePosters(focus, previous, next: null);

            await UntilCacheHasAsync(cache, 99u);
            CollectionAssert.AreEqual(
                new[] { SteamPosterSource.CdnUrl(99u) },
                handler.RequestedUrls,
                "the cached focal and the absent next are not requested; only the missing flank is");
        }
        finally
        {
            http.Dispose();
        }
    }

    /// <summary>Waits (bounded) until the cache holds a poster for the app id (the fire-and-forget fetch lands).</summary>
    private static async Task UntilCacheHasAsync(SteamPosterCache cache, uint appId)
    {
        for (int i = 0; i < 200 && !cache.TryGet(appId, out _); i++)
            await Task.Delay(25).ConfigureAwait(false);
    }

    /// <summary>A tiny valid JPEG (the poster decode must succeed for the cache to populate).</summary>
    private static byte[] MakeJpegBytes()
    {
        using SKBitmap bitmap = new(8, 8, SKColorType.Rgb565, SKAlphaType.Opaque);
        bitmap.Erase(SKColors.Red);
        using SKImage? image = SKImage.FromBitmap(bitmap);
        using SKData? data = image?.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data?.ToArray() ?? [];
    }
}
