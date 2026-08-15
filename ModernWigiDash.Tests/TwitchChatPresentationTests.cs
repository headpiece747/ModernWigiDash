using ModernWigiDash.Widgets.Twitch;
using SkiaSharp;

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

    [TestMethod]
    public void StatusColor_ConnectedIsGreen_AllOtherStatesWhite()
    {
        Assert.AreEqual(new SKColor(0x10, 0xB9, 0x81), TwitchChatPresentation.StatusColor(ChatStatus.Connected),
            "a live chat reads green");
        Assert.AreEqual(SKColors.White, TwitchChatPresentation.StatusColor(ChatStatus.Connecting));
        Assert.AreEqual(SKColors.White, TwitchChatPresentation.StatusColor(ChatStatus.Disconnected));
    }

    [TestMethod]
    public void ClampMaxMessages_WithinRange_PassesThrough()
    {
        Assert.AreEqual(30, TwitchChatPresentation.ClampMaxMessages(30));
    }

    [TestMethod]
    public void ClampMaxMessages_BelowMinimum_ClampsToFive()
    {
        Assert.AreEqual(5, TwitchChatPresentation.ClampMaxMessages(0));
        Assert.AreEqual(5, TwitchChatPresentation.ClampMaxMessages(5));
    }

    [TestMethod]
    public void ClampMaxMessages_AboveMaximum_ClampsToOneHundred()
    {
        Assert.AreEqual(100, TwitchChatPresentation.ClampMaxMessages(500));
        Assert.AreEqual(100, TwitchChatPresentation.ClampMaxMessages(100));
    }
}
