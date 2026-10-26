namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The persona provider's pins: the avatar retirement discipline (the UAF
/// rule: a replaced avatar is retired, disposed on the render thread's
/// drain, never on the background thread that replaced it), the publish on
/// persona change, and the teardown drain. Disposal is observed through the
/// retirement set's pending count (the house pattern, SkiaSharp's
/// IsDisposed is internal).
/// </summary>
[TestClass]
public class SteamPersonaProviderTests
{
    private static (SteamPersonaProvider Provider, List<SteamPersona?> Published) Create()
    {
        var published = new List<SteamPersona?>();
        // The production client is dormant without credentials (zero HTTP),
        // so no fetch runs in these pins.
        return (new SteamPersonaProvider(new SteamWebApiClient(), persona => published.Add(persona)), published);
    }

    [TestMethod]
    public void SetPersonaForTest_PublishesThePersona()
    {
        var (provider, published) = Create();
        var persona = new SteamPersona("http://avatar", "Name", 1);

        provider.SetPersonaForTest(persona, null);

        Assert.AreSame(persona, provider.Persona);
        Assert.AreEqual(1, published.Count);
        Assert.AreSame(persona, published[0]);
        provider.Dispose();
    }

    [TestMethod]
    public void ReplacingTheAvatar_RetiresThePreviousForTheRenderThreadDrain()
    {
        var (provider, _) = Create();
        var first = new SKBitmap(2, 2, SKColorType.Rgb565, SKAlphaType.Opaque);
        var second = new SKBitmap(2, 2, SKColorType.Rgb565, SKAlphaType.Opaque);

        provider.SetPersonaForTest(new SteamPersona("u1", "n", 1), first);
        provider.SetPersonaForTest(new SteamPersona("u2", "n", 1), second);

        Assert.AreSame(second, provider.Avatar);
        Assert.AreEqual(1, provider.RetirementSet.PendingCount, "a replaced avatar is retired, never freed by the replacing thread (the UAF rule)");

        provider.DisposeRetired();
        Assert.AreEqual(0, provider.RetirementSet.PendingCount, "the render-thread drain frees the retired avatar");
        provider.Dispose();
    }

    [TestMethod]
    public void ClearingThePersona_RetiresTheCurrentAvatarAndPublishes()
    {
        var (provider, published) = Create();
        var avatar = new SKBitmap(2, 2, SKColorType.Rgb565, SKAlphaType.Opaque);

        provider.SetPersonaForTest(new SteamPersona("u", "n", 1), avatar);
        provider.SetPersonaForTest(null, null);

        Assert.IsNull(provider.Persona);
        Assert.IsNull(provider.Avatar);
        Assert.AreEqual(1, provider.RetirementSet.PendingCount);
        Assert.AreEqual(2, published.Count);
        Assert.IsNull(published[1], "the clear must publish so the snapshot drops the persona");

        provider.Dispose();
        Assert.AreEqual(0, provider.RetirementSet.PendingCount, "teardown drains the pending retirement");
    }

    [TestMethod]
    public void SetCredentials_BlankPair_ClearsThePersona()
    {
        var (provider, _) = Create();
        var avatar = new SKBitmap(2, 2, SKColorType.Rgb565, SKAlphaType.Opaque);

        provider.SetPersonaForTest(new SteamPersona("u", "n", 1), avatar);
        provider.SetCredentials("", "");

        Assert.IsNull(provider.Persona);
        Assert.IsNull(provider.Avatar);
        provider.Dispose();
    }

    [TestMethod]
    public void Dispose_DrainsTheLiveAvatarAndPendingRetirements()
    {
        var (provider, _) = Create();
        var first = new SKBitmap(2, 2, SKColorType.Rgb565, SKAlphaType.Opaque);
        var second = new SKBitmap(2, 2, SKColorType.Rgb565, SKAlphaType.Opaque);

        provider.SetPersonaForTest(new SteamPersona("u", "n", 1), first);
        provider.SetPersonaForTest(new SteamPersona("u2", "n", 1), second);

        Assert.AreEqual(1, provider.RetirementSet.PendingCount);
        provider.Dispose();

        Assert.IsNull(provider.Avatar, "the live avatar is released at teardown");
        Assert.AreEqual(0, provider.RetirementSet.PendingCount, "the pending retirement drains at teardown");
    }
}
