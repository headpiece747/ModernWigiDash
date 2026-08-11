using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// The crash-log message sanitizer (CrashLog.SanitizeMessage): exception text
/// may embed a URL carrying a token (an API error echoing the failing
/// request), and crash.log is plaintext next to the executable — so token
/// values are redacted and query strings are stripped before a line lands in
/// the log.
/// </summary>
[TestClass]
public class AppCrashLogTests
{
    [TestMethod]
    public void SanitizeMessage_RedactsTokenValues()
    {
        Assert.AreEqual("failed: token=<redacted>", CrashLog.SanitizeMessage("failed: token=secret123"));
    }

    [TestMethod]
    public void SanitizeMessage_RedactsTokenCaseInsensitively()
    {
        // The match is case-insensitive; the marker is a fixed lowercase token.
        Assert.AreEqual("token=<redacted>", CrashLog.SanitizeMessage("TOKEN=SuperSecret"));
    }

    [TestMethod]
    public void SanitizeMessage_StripsQueryFromEmbeddedUrl()
    {
        string result = CrashLog.SanitizeMessage("GET https://api.twitch.tv/device?token=secret123&scope=chat failed");

        Assert.AreEqual("GET https://api.twitch.tv/device failed", result);
    }

    [TestMethod]
    public void SanitizeMessage_PlainMessage_IsUnchanged()
    {
        const string plain = "Object reference not set to an instance of an object";
        Assert.AreEqual(plain, CrashLog.SanitizeMessage(plain));
    }

    [TestMethod]
    public void SanitizeMessage_NullOrEmpty_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, CrashLog.SanitizeMessage(null));
        Assert.AreEqual(string.Empty, CrashLog.SanitizeMessage(""));
    }
}
