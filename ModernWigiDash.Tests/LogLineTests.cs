using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// The log-line rule (LogLine.Sanitize): the single owner of the shape every
/// log value must have before it reaches a line-oriented log file — newlines
/// flattened, tokens and URL queries redacted, length bounded. The widget
/// host sink (MainWindow.LogInfo/LogError) and the crash log route through
/// it, so the rule is pinned once instead of mirrored per module. The
/// redaction pins moved here from the retired AppCrashLogTests with the rule
/// they covered.
/// </summary>
[TestClass]
public class LogLineTests
{
    [TestMethod]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, LogLine.Sanitize(null));
        Assert.AreEqual(string.Empty, LogLine.Sanitize(""));
    }

    [TestMethod]
    public void Sanitize_MultiLineInput_FlattensToASingleLine()
    {
        string result = LogLine.Sanitize("line one\nline two\rline three");

        Assert.AreEqual("line one line two line three", result,
            "a multi-line value (an ex.ToString() stack trace) must reach the line-oriented log as one line");
    }

    [TestMethod]
    public void Sanitize_OversizedInput_IsBoundedToTheLineCap()
    {
        string result = LogLine.Sanitize(new string('x', LogLine.MaxLineLength * 2));

        Assert.AreEqual(LogLine.MaxLineLength, result.Length,
            "an oversized value cannot write a multi-megabyte line into the line-oriented log");
    }

    [TestMethod]
    public void Sanitize_RedactsTokenValues()
    {
        Assert.AreEqual("failed: token=<redacted>", LogLine.Sanitize("failed: token=secret123"));
    }

    [TestMethod]
    public void Sanitize_RedactsCredentialShapedParams()
    {
        // The redactor covers every credential-shaped query param, not just
        // `token=` — access/refresh/device tokens are equally sensitive. The
        // marker is the fixed "token=<redacted>" form for any match.
        Assert.AreEqual("token=<redacted>", LogLine.Sanitize("access_token=abc123"));
        Assert.AreEqual("token=<redacted>", LogLine.Sanitize("refresh_token=abc123"));
        Assert.AreEqual("token=<redacted>", LogLine.Sanitize("device_code=abc123"));
    }

    [TestMethod]
    public void Sanitize_RedactsTokenCaseInsensitively()
    {
        // The match is case-insensitive; the marker is a fixed lowercase token.
        Assert.AreEqual("token=<redacted>", LogLine.Sanitize("TOKEN=SuperSecret"));
    }

    [TestMethod]
    public void Sanitize_StripsQueryFromEmbeddedUrl()
    {
        string result = LogLine.Sanitize("GET https://api.twitch.tv/device?token=secret123&scope=chat failed");

        Assert.AreEqual("GET https://api.twitch.tv/device failed", result);
    }

    [TestMethod]
    public void Sanitize_PlainMessage_IsUnchanged()
    {
        const string plain = "Object reference not set to an instance of an object";
        Assert.AreEqual(plain, LogLine.Sanitize(plain));
    }
}
