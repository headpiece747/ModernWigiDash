using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Twitch chat connection-state policy — the NOTICE → connection-state
/// transition and the message-buffer bound, moved out of the presentation
/// tests when the policy module was split from the display rules.
/// </summary>
[TestClass]
public class TwitchChatStatusPolicyTests
{
    [TestMethod]
    public void StatusFromNotice_LoginAuthenticationFailed_Disconnects()
    {
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("Login authentication failed", ChatStatus.Connecting);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_InvalidNick_Disconnects()
    {
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("Invalid NICK", ChatStatus.Connecting);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_LoginFailureMatchingIsCaseInsensitive()
    {
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("LOGIN AUTHENTICATION FAILED", ChatStatus.Connecting);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_NotLoggedIn_Connects()
    {
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("you are not logged in", ChatStatus.Disconnected);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Connected, status);
    }

    [TestMethod]
    public void StatusFromNotice_AnyOtherNotice_KeepsTheCurrentStatus()
    {
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("This room is in slow mode", ChatStatus.Connected);
        Assert.IsFalse(changed);
        Assert.AreEqual(ChatStatus.Connected, status);
    }

    [TestMethod]
    public void StatusFromNotice_KeywordEmbeddedInLongerNotice_StillMatches()
    {
        // Real NOTICEs carry a server prefix / appended reason — the match
        // is substring-based, and that is part of the contract.
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice(
            ":tmi.twitch.tv NOTICE #channel :Login authentication failed — try again", ChatStatus.Connecting);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_FailureWhileAlreadyConnected_Disconnects()
    {
        // The policy is the only mechanism that leaves the LIVE state: an
        // auth failure arriving while chat is Connected must kick it out.
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("Login authentication failed", ChatStatus.Connected);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_LoginUnsuccessful_Disconnects()
    {
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("Login unsuccessful", ChatStatus.Connecting);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_ImproperlyFormattedAuth_Disconnects()
    {
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("Improperly formatted auth", ChatStatus.Connecting);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_FailureWhileAlreadyDisconnected_IsNotATransition()
    {
        // A repeated failure notice landing on the state the rule already
        // set is not a transition: the connection logs it as a notice
        // instead of re-logging the failure and re-publishing the state.
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("Login authentication failed", ChatStatus.Disconnected);
        Assert.IsFalse(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_NotLoggedInWhileAlreadyConnected_IsNotATransition()
    {
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("you are not logged in", ChatStatus.Connected);
        Assert.IsFalse(changed);
        Assert.AreEqual(ChatStatus.Connected, status);
    }

    [TestMethod]
    public void StatusFromNotice_EmptyNotice_KeepsTheCurrentStatus()
    {
        var (status, changed) = TwitchChatStatusPolicy.StatusFromNotice("", ChatStatus.Connecting);
        Assert.IsFalse(changed);
        Assert.AreEqual(ChatStatus.Connecting, status);
    }

    [TestMethod]
    public void ClampMaxMessages_WithinRange_PassesThrough()
    {
        Assert.AreEqual(30, TwitchChatStatusPolicy.ClampMaxMessages(30));
        Assert.AreEqual(6, TwitchChatStatusPolicy.ClampMaxMessages(6));
        Assert.AreEqual(99, TwitchChatStatusPolicy.ClampMaxMessages(99));
    }

    [TestMethod]
    public void ClampMaxMessages_BelowMinimum_ClampsToFive()
    {
        Assert.AreEqual(5, TwitchChatStatusPolicy.ClampMaxMessages(0));
        Assert.AreEqual(5, TwitchChatStatusPolicy.ClampMaxMessages(-1));
        Assert.AreEqual(5, TwitchChatStatusPolicy.ClampMaxMessages(4));
        Assert.AreEqual(5, TwitchChatStatusPolicy.ClampMaxMessages(5));
    }

    [TestMethod]
    public void ClampMaxMessages_AboveMaximum_ClampsToOneHundred()
    {
        Assert.AreEqual(100, TwitchChatStatusPolicy.ClampMaxMessages(500));
        Assert.AreEqual(100, TwitchChatStatusPolicy.ClampMaxMessages(101));
        Assert.AreEqual(100, TwitchChatStatusPolicy.ClampMaxMessages(100));
    }
}
