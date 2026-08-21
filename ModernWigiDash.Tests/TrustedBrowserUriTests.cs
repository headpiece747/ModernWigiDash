
namespace ModernWigiDash.Tests;

/// <summary>
/// The shell-open trust rule (<see cref="TrustedBrowserUri"/>) used by the
/// device-authorization dialog's "Open Twitch" button: only https URLs on
/// twitch.tv may reach Process.Start, so a tampered verification response
/// cannot invoke file:/custom protocol handlers.
/// </summary>
[TestClass]
public class TrustedBrowserUriTests
{
    [TestMethod]
    [DataRow("https://id.twitch.tv/oauth2/authorize?token=x")]
    [DataRow("https://www.twitch.tv/device")]
    [DataRow("https://twitch.tv/device")]
    public void IsTrusted_HttpsTwitchTvUrls_AreTrusted(string url)
    {
        Assert.IsTrue(TrustedBrowserUri.IsTrusted(new Uri(url)));
    }

    [TestMethod]
    [DataRow("http://id.twitch.tv/device", "plain http must be rejected")]
    [DataRow("https://twitch.tv.evil.example/device", "a host merely ENDING in twitch.tv must be rejected")]
    [DataRow("https://example.com/device", "a non-Twitch host must be rejected")]
    [DataRow("https://evil.com/?next=twitch.tv", "a twitch.tv host only inside the query must be rejected")]
    [DataRow("file:///C:/Windows/notepad.exe", "file URLs must be rejected")]
    [DataRow("ftp://twitch.tv/device", "non-https schemes must be rejected")]
    public void IsTrusted_NonHttpsOrNonTwitchUrls_AreRejected(string url, string because)
    {
        Assert.IsFalse(TrustedBrowserUri.IsTrusted(new Uri(url)), because);
    }
}
