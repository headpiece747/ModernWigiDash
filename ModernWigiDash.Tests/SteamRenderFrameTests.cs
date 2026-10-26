using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

/// <summary>
/// The service's render frame (the narrowed render face): one call owns the
/// retired-texture drain (the poster cache's and the avatar's, both) and
/// hands back the poster lookup seam + the avatar, so the render thread no
/// longer spells the paired drain ritual and never sees the concrete cache.
/// </summary>
[TestClass]
public class SteamRenderFrameTests
{
    private static (SteamLibraryService Service, SteamPosterCache Cache) Create()
    {
        var service = new SteamLibraryService(
            new EmptySource(),
            new SteamProcessTracker(new NoOpBridge(), _ => { }),
            new SteamWebApiClient(),
            _ => { },
            _ => { });
        return (service, service.PosterCacheForTest);
    }

    [TestMethod]
    public void BeginRenderFrame_DrainsBothRetiredSets()
    {
        (SteamLibraryService service, SteamPosterCache cache) = Create();
        var persona = new SteamPersona("https://example.com/a.jpg", "Tester", 2);
        using var avatarOne = new SKBitmap(8, 8);
        using var poster = new SKBitmap(8, 8, SKColorType.Rgb565, SKAlphaType.Opaque);

        // Retire one poster texture and one avatar texture (the replacement rule).
        cache.RetirementSet.Retire(poster);
        service.SetPersonaForTest(persona, avatarOne);
        using var avatarTwo = new SKBitmap(8, 8);
        service.SetPersonaForTest(persona, avatarTwo);

        SteamRenderFrame frame = service.BeginRenderFrame();

        Assert.AreEqual(0, cache.RetirementSet.PendingCount, "the poster cache's retired set drains on the render thread");
        Assert.AreSame(cache, frame.Posters, "the frame carries the poster lookup seam");
        Assert.AreSame(avatarTwo, frame.Avatar, "the frame carries the live avatar");
    }

    [TestMethod]
    public void BeginRenderFrame_NoRetired_LeavesBothSetsEmpty()
    {
        (SteamLibraryService service, SteamPosterCache cache) = Create();

        SteamRenderFrame frame = service.BeginRenderFrame();

        Assert.AreEqual(0, cache.RetirementSet.PendingCount);
        Assert.IsNull(frame.Avatar, "no credentials, no avatar (the banner draws its placeholder)");
    }

    /// <summary>The empty library source (no manifests, no path).</summary>
    private sealed class EmptySource : ISteamLibrarySource
    {
        public string? SteamPath => null;
        public IReadOnlyList<SteamAppManifest> LoadManifests(string? steamPath) => [];
    }

    /// <summary>The no-op process bridge (nothing running, nothing to close).</summary>
    private sealed class NoOpBridge : ISteamProcessBridge
    {
        public bool IsRunning(string processName) => false;
        public bool TryClose(string processName, TimeSpan budget) => true;
    }
}
