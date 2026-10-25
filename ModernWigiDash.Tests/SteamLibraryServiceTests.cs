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
}
