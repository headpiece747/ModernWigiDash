using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Twitch chat widget's display rules — the status line and the
/// empty-state hint, previously composed inline in the render path with no
/// tests at all.
/// </summary>
[TestClass]
public class TwitchChatPresentationTests
{
    [TestMethod]
    public void StatusText_WithoutDetail_UsesStateDefaults()
    {
        Assert.AreEqual("● LIVE", TwitchChatPresentation.StatusText(ChatStatus.Connected, ""));
        Assert.AreEqual("⟳ Connecting…", TwitchChatPresentation.StatusText(ChatStatus.Connecting, ""));
        Assert.AreEqual("○ Disconnected", TwitchChatPresentation.StatusText(ChatStatus.Disconnected, ""));
    }

    [TestMethod]
    public void StatusText_WithDetail_ShowsTheDetailAfterTheDot()
    {
        Assert.AreEqual("● Watching stream", TwitchChatPresentation.StatusText(ChatStatus.Connected, "Watching stream"));
        Assert.AreEqual("⟳ Reconnecting in 5s", TwitchChatPresentation.StatusText(ChatStatus.Connecting, "Reconnecting in 5s"));
        Assert.AreEqual("○ Login required", TwitchChatPresentation.StatusText(ChatStatus.Disconnected, "Login required"));
    }

    [TestMethod]
    public void EmptyHint_StateAndAutoConnect_DetermineThePrompt()
    {
        Assert.AreEqual("Waiting for chat…", TwitchChatPresentation.EmptyHint(ChatStatus.Connected, true));
        Assert.AreEqual("Waiting for chat…", TwitchChatPresentation.EmptyHint(ChatStatus.Connected, false));
        Assert.AreEqual("Tap to connect", TwitchChatPresentation.EmptyHint(ChatStatus.Disconnected, false),
            "manual mode invites a tap");
        Assert.AreEqual("Waiting for connection…", TwitchChatPresentation.EmptyHint(ChatStatus.Disconnected, true));
        Assert.AreEqual("Waiting for connection…", TwitchChatPresentation.EmptyHint(ChatStatus.Connecting, true));
    }
}
