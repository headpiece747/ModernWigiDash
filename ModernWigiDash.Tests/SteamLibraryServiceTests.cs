using System.IO;
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
        public IReadOnlyList<SteamAppManifest> LoadManifests() => [];
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
        public IReadOnlyList<SteamAppManifest> LoadManifests()
            => [new SteamAppManifest(730u, "Game", "gamedir", "game.exe", SteamAppState.FullyInstalled, 0, 0, 0, 10.0)];
    }

    /// <summary>A service over inert seams, capturing every published snapshot.</summary>
    private static (SteamLibraryService Service, List<SteamLibrarySnapshot> Published) Create()
    {
        List<SteamLibrarySnapshot> published = [];
        var service = new SteamLibraryService(
            new EmptySource(),
            new SteamProcessTracker(new NoOpBridge(), _ => { }),
            new SteamPosterCache(),
            new SteamWebApiClient(),
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
        Assert.AreSame(avatar, service.PersonaAvatar);
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
        Assert.IsNull(service.PersonaAvatar);
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
        Assert.IsNull(service.PersonaAvatar);
    }

    [TestMethod]
    public void LoadLocalPosterForTest_PopulatesTheCacheFromTheLocalFile()
    {
        string temp = Path.Combine(Path.GetTempPath(), "wmd-steam-poster-" + Guid.NewGuid().ToString("N"));
        string posterDir = Path.Combine(temp, "appcache", "librarycache", "730");
        Directory.CreateDirectory(posterDir);
        File.WriteAllBytes(Path.Combine(posterDir, SteamPosterSource.PosterFileName), MakeJpegBytes());

        var service = new SteamLibraryService(
            new TempSource(temp),
            new SteamProcessTracker(new NoOpBridge(), _ => { }),
            new SteamPosterCache(),
            new SteamWebApiClient(),
            _ => { },
            _ => { });

        try
        {
            Assert.IsFalse(service.PosterCache.TryGet(730u, out _), "no poster before the load");

            service.LoadLocalPosterForTest(730u);

            Assert.IsTrue(service.PosterCache.TryGet(730u, out _), "the local poster must land in the cache");
        }
        finally
        {
            service.Dispose();
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

        public IReadOnlyList<SteamAppManifest> LoadManifests()
            => [new SteamAppManifest(730u, "Game", "gamedir", "game.exe", SteamAppState.FullyInstalled, 0, 0, 0, 10.0)];
    }

    [TestMethod]
    public async Task Terminate_ClosesTheGamesProcessTree()
    {
        var bridge = new RecordingBridge();
        using var service = new SteamLibraryService(
            new OneGameSource(),
            new SteamProcessTracker(bridge, _ => { }),
            new SteamPosterCache(),
            new SteamWebApiClient(),
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
        using var service = new SteamLibraryService(
            new OneGameSource(),
            new SteamProcessTracker(bridge, _ => { }),
            new SteamPosterCache(),
            new SteamWebApiClient(),
            _ => { },
            _ => { });
        service.RefreshNow();

        await service.Terminate(999u);

        Assert.IsEmpty(bridge.Closed, "an app id with no process name must not close anything");
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
