using System.Linq;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Twitch IRC wire-format parser (TwitchIrcMessages) plus the NOTICE →
/// status rules (TwitchChatPresentation.StatusFromNotice) — the protocol was
/// extracted from TwitchChatStreamWidget so the framing, tags, escapes, the
/// 400-char cap, and the name palette are directly testable.
/// </summary>
[TestClass]
public class TwitchIrcMessagesTests
{
    [TestMethod]
    public void TryParse_PingWithPayload_ParsesThePongPayload()
    {
        Assert.IsTrue(TwitchIrcMessages.TryParse("PING :tmi.twitch.tv", out var message));
        Assert.AreEqual(IrcMessageKind.Ping, message.Kind);
        Assert.AreEqual("tmi.twitch.tv", message.PingPayload);
    }

    [TestMethod]
    public void TryParse_BarePing_ParsesWithEmptyPayload()
    {
        Assert.IsTrue(TwitchIrcMessages.TryParse("PING", out var message));
        Assert.AreEqual(IrcMessageKind.Ping, message.Kind);
        Assert.AreEqual("", message.PingPayload);
    }

    [TestMethod]
    public void TryParse_PrivmsgWithTags_ExtractsDisplayNameLoginColorAndText()
    {
        const string line = "@display-name=GamerOne;login=gamerone;color=#FF79C6 :gamerone!gamerone@gamerone.tmi.twitch.tv PRIVMSG #test :hello world";
        Assert.IsTrue(TwitchIrcMessages.TryParse(line, out var message));
        Assert.AreEqual(IrcMessageKind.Privmsg, message.Kind);
        Assert.AreEqual("GamerOne", message.Username);
        Assert.AreEqual("gamerone", message.Login);
        Assert.AreEqual("#FF79C6", message.ColorHex);
        Assert.AreEqual("hello world", message.Text);
    }

    [TestMethod]
    public void TryParse_PrivmsgWithoutTags_FallsBackToUser()
    {
        Assert.IsTrue(TwitchIrcMessages.TryParse(":gamerone!gamerone@gamerone.tmi.twitch.tv PRIVMSG #test :hi", out var message));
        Assert.AreEqual(IrcMessageKind.Privmsg, message.Kind);
        Assert.AreEqual("user", message.Username);
        Assert.AreEqual("", message.ColorHex);
        Assert.AreEqual("hi", message.Text);
    }

    [TestMethod]
    public void TryParse_PrivmsgWithOnlyLogin_UsesTheLoginAsUsername()
    {
        Assert.IsTrue(TwitchIrcMessages.TryParse("@login=gamerone :gamerone!gamerone@gamerone.tmi.twitch.tv PRIVMSG #test :hi", out var message));
        Assert.AreEqual(IrcMessageKind.Privmsg, message.Kind);
        Assert.AreEqual("gamerone", message.Username);
        Assert.AreEqual("gamerone", message.Login);
    }

    [TestMethod]
    public void TryParse_PrivmsgText_UnescapesTwitchSequences()
    {
        Assert.IsTrue(TwitchIrcMessages.TryParse(":gamerone!gamerone@gamerone.tmi.twitch.tv PRIVMSG #test :hello\\sworld\\:)", out var message));
        Assert.AreEqual("hello world;)", message.Text);
    }

    [TestMethod]
    public void TryParse_PrivmsgLongerThan400Chars_TruncatesTo400()
    {
        var text = new string('x', 500);
        Assert.IsTrue(TwitchIrcMessages.TryParse(":gamerone!gamerone@gamerone.tmi.twitch.tv PRIVMSG #test :" + text, out var message));
        Assert.AreEqual(400, message.Text.Length);
        Assert.AreEqual(new string('x', 400), message.Text);
    }

    [TestMethod]
    public void TryParse_Notice_ParsesTheRawNoticeText()
    {
        Assert.IsTrue(TwitchIrcMessages.TryParse(":tmi.twitch.tv NOTICE #test :Login authentication failed", out var message));
        Assert.AreEqual(IrcMessageKind.Notice, message.Kind);
        Assert.AreEqual("Login authentication failed", message.Text);
    }

    [TestMethod]
    public void TryParse_RoomState_ParsesAsRoomState()
    {
        Assert.IsTrue(TwitchIrcMessages.TryParse(":tmi.twitch.tv ROOMSTATE #test", out var message));
        Assert.AreEqual(IrcMessageKind.RoomState, message.Kind);
    }

    [TestMethod]
    public void TryParse_UnknownCommand_ParsesAsOther()
    {
        Assert.IsTrue(TwitchIrcMessages.TryParse(":tmi.twitch.tv 001 gamerone :Welcome", out var message));
        Assert.AreEqual(IrcMessageKind.Other, message.Kind);
    }

    [TestMethod]
    public void TryParse_MalformedLines_ReturnFalse()
    {
        Assert.IsFalse(TwitchIrcMessages.TryParse("", out _));
        Assert.IsFalse(TwitchIrcMessages.TryParse("justnick", out _));
        Assert.IsFalse(TwitchIrcMessages.TryParse("@tags-without-space", out _));
    }

    [TestMethod]
    public void PaletteColorFor_SameName_IsStableAndInPalette()
    {
        var color = TwitchIrcMessages.PaletteColorFor("gamerone");
        Assert.AreEqual(color, TwitchIrcMessages.PaletteColorFor("gamerone"));
        Assert.IsTrue(TwitchIrcMessages.NamePalette.Contains(color), "The hash must land on a palette entry");
    }

    [TestMethod]
    public void PaletteColorFor_OneCharacterName_HashesToTheKnownSlot()
    {
        // hash(17, 'a'=97) = 17*31+97 = 624; 624 % 8 = 0.
        Assert.AreEqual(TwitchIrcMessages.NamePalette[0], TwitchIrcMessages.PaletteColorFor("a"));
    }

    [TestMethod]
    public void StatusFromNotice_LoginAuthenticationFailed_Disconnects()
    {
        var (status, changed) = TwitchChatPresentation.StatusFromNotice("Login authentication failed", ChatStatus.Connecting);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_InvalidNick_Disconnects()
    {
        var (status, changed) = TwitchChatPresentation.StatusFromNotice("Invalid NICK", ChatStatus.Connecting);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_LoginFailureMatchingIsCaseInsensitive()
    {
        var (status, changed) = TwitchChatPresentation.StatusFromNotice("LOGIN AUTHENTICATION FAILED", ChatStatus.Connecting);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Disconnected, status);
    }

    [TestMethod]
    public void StatusFromNotice_NotLoggedIn_Connects()
    {
        var (status, changed) = TwitchChatPresentation.StatusFromNotice("you are not logged in", ChatStatus.Disconnected);
        Assert.IsTrue(changed);
        Assert.AreEqual(ChatStatus.Connected, status);
    }

    [TestMethod]
    public void StatusFromNotice_AnyOtherNotice_KeepsTheCurrentStatus()
    {
        var (status, changed) = TwitchChatPresentation.StatusFromNotice("This room is in slow mode", ChatStatus.Connected);
        Assert.IsFalse(changed);
        Assert.AreEqual(ChatStatus.Connected, status);
    }
}
