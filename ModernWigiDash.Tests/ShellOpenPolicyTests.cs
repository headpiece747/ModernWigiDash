namespace ModernWigiDash.Tests;

/// <summary>
/// The shell-open trust rule pinned at its module interface (no spawn, no OS):
/// only http, https, and mailto URLs may be handed to the OS default handler; a
/// file:, ftp:, javascript:, or non-absolute value is refused before the spawn.
/// This is the one gate every shell-open site routes through (the hotkey
/// executor's OpenUrl action and the calendar widget's meeting-link open), so
/// the scheme allowlist has one owner and cannot drift between the two.
/// </summary>
[TestClass]
public class ShellOpenPolicyTests
{
    [TestMethod]
    public void IsAllowedUrl_OnlyHttpHttpsMailto()
    {
        Assert.IsTrue(ShellOpenPolicy.IsAllowedUrl("https://example.com"));
        Assert.IsTrue(ShellOpenPolicy.IsAllowedUrl("http://example.com"));
        Assert.IsTrue(ShellOpenPolicy.IsAllowedUrl("mailto:someone@example.com"));
        Assert.IsFalse(ShellOpenPolicy.IsAllowedUrl("ftp://example.com"));
        Assert.IsFalse(ShellOpenPolicy.IsAllowedUrl("javascript:alert(1)"));
        Assert.IsFalse(ShellOpenPolicy.IsAllowedUrl("file:///etc/passwd"));
        Assert.IsFalse(ShellOpenPolicy.IsAllowedUrl("not a url"));
        Assert.IsFalse(ShellOpenPolicy.IsAllowedUrl(""));
    }
}
