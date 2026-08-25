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

    [TestMethod]
    [DataRow("https://id.twitch.tv/oauth2/authorize?token=x")]
    [DataRow("https://www.twitch.tv/device")]
    [DataRow("https://twitch.tv/device")]
    public void IsTwitchAuthorizationUri_HttpsTwitchTvUrls_AreTrusted(string url)
    {
        Assert.IsTrue(ModernWigiDash.Sdk.TrustedUriPolicy.IsTwitchAuthorizationUri(new Uri(url)));
    }

    [TestMethod]
    [DataRow("http://id.twitch.tv/device", "plain http must be rejected")]
    [DataRow("https://twitch.tv.evil.example/device", "a host merely ENDING in twitch.tv must be rejected")]
    [DataRow("https://example.com/device", "a non-Twitch host must be rejected")]
    [DataRow("https://evil.com/?next=twitch.tv", "a twitch.tv host only inside the query must be rejected")]
    [DataRow("file:///C:/Windows/notepad.exe", "file URLs must be rejected")]
    [DataRow("ftp://twitch.tv/device", "non-https schemes must be rejected")]
    public void IsTwitchAuthorizationUri_NonHttpsOrNonTwitchUrls_AreRejected(string url, string because)
    {
        Assert.IsFalse(ModernWigiDash.Sdk.TrustedUriPolicy.IsTwitchAuthorizationUri(new Uri(url)), because);
    }
}
