using ModernWigiDash.Widgets.Twitch;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Twitch chat widget's display rules — the status line and the
/// empty-state hint, previously composed inline in the render path with no
/// tests at all. The connection-state policy is pinned by
/// <see cref="TwitchChatStatusPolicyTests"/>.
/// </summary>
[TestClass]
public class TwitchChatPresentationTests
{
    [TestMethod]
    public void ChatState_Factories_SpellEveryTransitionPayload()
    {
        // The payload factories are the ONE spelling of the status details —
        // the widget's state-change sites store these, none compose their own.
        Assert.AreEqual(new TwitchChatPresentation.ChatState(ChatStatus.Disconnected, ""), TwitchChatPresentation.ChatState.Disconnected());
        Assert.AreEqual(new TwitchChatPresentation.ChatState(ChatStatus.Connecting, "Connecting…"), TwitchChatPresentation.ChatState.Connecting());
        Assert.AreEqual(new TwitchChatPresentation.ChatState(ChatStatus.Connecting, "Joining #mychannel…"), TwitchChatPresentation.ChatState.JoiningChannel("mychannel"));
        Assert.AreEqual(new TwitchChatPresentation.ChatState(ChatStatus.Disconnected, "Reconnecting…"), TwitchChatPresentation.ChatState.Reconnecting());
        Assert.AreEqual(new TwitchChatPresentation.ChatState(ChatStatus.Connected, "LIVE"), TwitchChatPresentation.ChatState.Live());
        Assert.AreEqual(new TwitchChatPresentation.ChatState(ChatStatus.Disconnected, "Login failed — check token & username"), TwitchChatPresentation.ChatState.LoginFailed());
    }

    [TestMethod]
    public void StatusText_EveryPayload_RendersTheStateDotAndDetail()
    {
        Assert.AreEqual("● LIVE", TwitchChatPresentation.StatusText(TwitchChatPresentation.ChatState.Live()));
        Assert.AreEqual("⟳ Connecting…", TwitchChatPresentation.StatusText(TwitchChatPresentation.ChatState.Connecting()));
        Assert.AreEqual("⟳ Joining #mychannel…", TwitchChatPresentation.StatusText(TwitchChatPresentation.ChatState.JoiningChannel("mychannel")));
        Assert.AreEqual("○ Reconnecting…", TwitchChatPresentation.StatusText(TwitchChatPresentation.ChatState.Reconnecting()));
        Assert.AreEqual("○ Login failed — check token & username", TwitchChatPresentation.StatusText(TwitchChatPresentation.ChatState.LoginFailed()));
        Assert.AreEqual("○ Disconnected", TwitchChatPresentation.StatusText(TwitchChatPresentation.ChatState.Disconnected()),
            "the plain-disconnected payload is the only empty-detail state, spelled by the presentation");
    }

    [TestMethod]
    public void StatusText_WithDetail_ShowsTheDetailAfterTheDot()
    {
        Assert.AreEqual("● Watching stream", TwitchChatPresentation.StatusText(new TwitchChatPresentation.ChatState(ChatStatus.Connected, "Watching stream")));
        Assert.AreEqual("⟳ Reconnecting in 5s", TwitchChatPresentation.StatusText(new TwitchChatPresentation.ChatState(ChatStatus.Connecting, "Reconnecting in 5s")));
        Assert.AreEqual("○ Login required", TwitchChatPresentation.StatusText(new TwitchChatPresentation.ChatState(ChatStatus.Disconnected, "Login required")));
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
}
