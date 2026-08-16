namespace ModernWigiDash.Tests;

[TestClass]
public class TrustedUriPolicyTests
{
    [TestMethod]
    public void IsTwitchAuthorizationHost_Apex_ReturnsTrue() =>
        Assert.IsTrue(ModernWigiDash.Sdk.TrustedUriPolicy.IsTwitchAuthorizationHost("twitch.tv"));

    [TestMethod]
    public void IsTwitchAuthorizationHost_Subdomain_ReturnsTrue() =>
        Assert.IsTrue(ModernWigiDash.Sdk.TrustedUriPolicy.IsTwitchAuthorizationHost("www.twitch.tv"));

    [TestMethod]
    public void IsTwitchAuthorizationHost_DeepSubdomain_ReturnsTrue() =>
        Assert.IsTrue(ModernWigiDash.Sdk.TrustedUriPolicy.IsTwitchAuthorizationHost("auth.example.twitch.tv"));

    [TestMethod]
    public void IsTwitchAuthorizationHost_CaseInsensitive_ReturnsTrue() =>
        Assert.IsTrue(ModernWigiDash.Sdk.TrustedUriPolicy.IsTwitchAuthorizationHost("TWITCH.TV"));

    [TestMethod]
    public void IsTwitchAuthorizationHost_LookalikeSuffix_ReturnsFalse() =>
        Assert.IsFalse(ModernWigiDash.Sdk.TrustedUriPolicy.IsTwitchAuthorizationHost("faketwitch.tv"));

    [TestMethod]
    public void IsTwitchAuthorizationHost_PrefixedLookalike_ReturnsFalse() =>
        Assert.IsFalse(ModernWigiDash.Sdk.TrustedUriPolicy.IsTwitchAuthorizationHost("evil-twitch.tv"));

    [TestMethod]
    public void IsTwitchAuthorizationHost_Unrelated_ReturnsFalse() =>
        Assert.IsFalse(ModernWigiDash.Sdk.TrustedUriPolicy.IsTwitchAuthorizationHost("example.com"));

    [TestMethod]
    public void IsTwitchAuthorizationHost_Null_ReturnsFalse() =>
        Assert.IsFalse(ModernWigiDash.Sdk.TrustedUriPolicy.IsTwitchAuthorizationHost(null));
}
