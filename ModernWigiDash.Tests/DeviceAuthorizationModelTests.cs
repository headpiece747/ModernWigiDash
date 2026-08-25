using System.Windows;
using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the device-authorization window's decision model at its interface
/// and without a window: the display facts, the trusted-open verdict (a
/// tampered URL is refused and logged before the shell-open seam runs), the
/// copy-code verdict, and the lifetime's slot-clear identity rule.
/// </summary>
[TestClass]
public class DeviceAuthorizationModelTests
{
    private static readonly DateTimeOffset ExpiresAt = new(2026, 8, 24, 12, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void DisplayFacts_AreTheSingleSpellings()
    {
        var model = new DeviceAuthorizationModel("Twitch", new Uri("https://www.twitch.tv/device"), "ABCD-EFGH", ExpiresAt, (_, _) => { });

        Assert.AreEqual("ModernWigiDash - Twitch Login", model.Title);
        Assert.AreEqual("Authorize Twitch in your browser", model.Header);
        Assert.AreEqual("ABCD-EFGH", model.Code);
        Assert.AreEqual("https://www.twitch.tv/device", model.VerificationText);
        Assert.AreEqual($"This code expires at {ExpiresAt.LocalDateTime:t}.", model.ExpirationText);
    }

    [TestMethod]
    public void Constructor_NullRequiredInputs_ThrowsArgumentNull()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new DeviceAuthorizationModel("Twitch", null!, "ABCD-EFGH", ExpiresAt, (_, _) => { }),
            "a null verification URI must be named at construction, not NRE at render");
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new DeviceAuthorizationModel("Twitch", new Uri("https://www.twitch.tv/device"), null!, ExpiresAt, (_, _) => { }),
            "a null user code must be named at construction, not NRE at copy");
        Assert.ThrowsExactly<ArgumentNullException>(
            () => new DeviceAuthorizationModel("Twitch", new Uri("https://www.twitch.tv/device"), "ABCD-EFGH", ExpiresAt, null!),
            "a null log seam must be named at construction, it would break the never-throw contract");
    }

    [TestMethod]
    public void OpenBrowser_LookalikeHost_RefusesLogsAndNeverOpens()
    {
        var log = new List<(string Message, Exception? Exception)>();
        var model = CreateModel("https://faketwitch.tv/device", log);
        var opened = new List<Uri>();

        model.OpenBrowser(opened.Add);

        Assert.AreEqual(0, opened.Count);
        Assert.AreEqual(1, log.Count);
        Assert.AreEqual("Refusing to open non-Twitch authorization URL", log[0].Message);
        Assert.IsNull(log[0].Exception);
    }

    [TestMethod]
    public void OpenBrowser_FileScheme_RefusesLogsAndNeverOpens()
    {
        var log = new List<(string Message, Exception? Exception)>();
        var model = CreateModel("file:///C:/evil.html", log);
        var opened = new List<Uri>();

        model.OpenBrowser(opened.Add);

        Assert.AreEqual(0, opened.Count);
        Assert.AreEqual("Refusing to open non-Twitch authorization URL", log[0].Message);
    }

    [TestMethod]
    public void OpenBrowser_TwitchHost_RunsTheOpenSeamWithoutLogging()
    {
        var log = new List<(string Message, Exception? Exception)>();
        var model = CreateModel("https://www.twitch.tv/device", log);
        var opened = new List<Uri>();

        model.OpenBrowser(opened.Add);

        Assert.AreEqual(1, opened.Count);
        Assert.AreEqual("https://www.twitch.tv/device", opened[0].AbsoluteUri);
        Assert.AreEqual(0, log.Count);
    }

    [TestMethod]
    public void OpenBrowser_OpenSeamThrows_LogsTheFailureLine()
    {
        var log = new List<(string Message, Exception? Exception)>();
        var model = CreateModel("https://twitch.tv/device", log);
        var boom = new InvalidOperationException("no browser");

        model.OpenBrowser(_ => throw boom);

        Assert.AreEqual(1, log.Count);
        Assert.AreEqual("Unable to open the Twitch authorization page", log[0].Message);
        Assert.AreSame(boom, log[0].Exception);
    }

    [TestMethod]
    public void CopyCode_SeamReceivesTheUserCode()
    {
        var log = new List<(string Message, Exception? Exception)>();
        var model = CreateModel("https://twitch.tv/device", log);
        string? copied = null;

        model.CopyCode(value => copied = value);

        Assert.AreEqual("WXYZ-1234", copied);
        Assert.AreEqual(0, log.Count);
    }

    [TestMethod]
    public void CopyCode_SeamThrows_LogsTheFailureLine()
    {
        var log = new List<(string Message, Exception? Exception)>();
        var model = CreateModel("https://twitch.tv/device", log);
        var boom = new InvalidOperationException("clipboard locked");

        model.CopyCode(_ => throw boom);

        Assert.AreEqual(1, log.Count);
        Assert.AreEqual("Unable to copy the authorization code", log[0].Message);
        Assert.AreSame(boom, log[0].Exception);
    }

    [TestMethod]
    public void ClosedWindowClearsSlot_OnlyTheCurrentWindowClearsTheSlot()
    {
        var verdict = StaRunner.Run(() =>
        {
            var current = new Window();
            var replaced = new Window();
            return (
                CurrentClears: DeviceAuthorizationModel.ClosedWindowClearsSlot(current, current),
                ReplacedDoesNotClear: DeviceAuthorizationModel.ClosedWindowClearsSlot(current, replaced),
                EmptySlotClearsNothing: DeviceAuthorizationModel.ClosedWindowClearsSlot(null, replaced));
        });

        Assert.IsTrue(verdict.CurrentClears);
        Assert.IsFalse(verdict.ReplacedDoesNotClear);
        Assert.IsFalse(verdict.EmptySlotClearsNothing);
    }

    private static DeviceAuthorizationModel CreateModel(string verificationUri, List<(string Message, Exception? Exception)> log)
        => new("Twitch", new Uri(verificationUri), "WXYZ-1234", ExpiresAt, (message, ex) => log.Add((message, ex)));
}
