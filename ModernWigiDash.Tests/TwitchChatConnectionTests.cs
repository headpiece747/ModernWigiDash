using System.IO;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

/// <summary>
/// Drives the Twitch chat connection module through the
/// <see cref="IWebSocketFeed"/> seam - no network, no ClientWebSocket, no
/// widget instance. The handshake payload, message parsing, the reconnect
/// backoff, the ChatState transition, and the buffer bound are exercised
/// here; the widget's wiring to the module is pinned by TwitchWidgetTests.
/// </summary>
[TestClass]
public class TwitchChatConnectionTests
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _loggedErrors = new();
    [TestMethod]
    public async Task Start_SendsHandshakeAndParsesPrivmsg()
    {
        var feed = new FakeFeed();
        feed.QueueMessage(":tmi.twitch.tv ROOMSTATE #test\r\n");
        feed.QueueMessage(":user!user@user.tmi.twitch.tv PRIVMSG #test :hello world\r\n");
        var (connection, states) = CreateRecordingConnection(feed);
        connection.Start("test");

        await TestWait.WaitUntilAsync(() => connection.MessageCount >= 1, TimeSpan.FromSeconds(3));

        Assert.IsTrue(feed.Sent.Any(s => s.StartsWith("CAP REQ", StringComparison.Ordinal)), "CAP handshake must be sent");
        Assert.IsTrue(feed.Sent.Any(s => s.StartsWith("PASS ", StringComparison.Ordinal)), "PASS must be sent");
        Assert.IsTrue(feed.Sent.Any(s => s.StartsWith("NICK justinfan", StringComparison.Ordinal)), "Anonymous NICK must be sent");
        Assert.IsTrue(feed.Sent.Contains("JOIN #test\r\n"), "JOIN must target the normalized channel");
        Assert.AreEqual(1, connection.MessageCount);
        // The ROOMSTATE lands between the handshake and the queue draining; by
        // the time the buffer is readable the cycle has already ended and the
        // state has moved on to the reconnect backoff, so the transition is
        // asserted from the recorded states, not the current one.
        Assert.IsTrue(states.Contains(TwitchChatPresentation.ChatState.Live()), "the ROOMSTATE line must set the live state");

        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task Start_CrlfChannelName_CannotInjectIrcLines()
    {
        // An imported channel with an embedded CRLF must not inject extra IRC
        // lines into the JOIN command - the whole name is rejected and the
        // default channel is joined instead.
        var feed = new FakeFeed();
        var connection = new TwitchChatConnection(() => feed, () => 30);
        connection.Start("x\r\nPRIVMSG #popular :spam");

        await TestWait.WaitUntilAsync(() => feed.Sent.Any(s => s.StartsWith("JOIN", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));

        Assert.IsTrue(feed.Sent.Contains("JOIN #twitch\r\n"), "a CRLF-bearing channel must fall back to the default channel");
        Assert.IsFalse(feed.Sent.Any(s => s.StartsWith("PRIVMSG", StringComparison.Ordinal)), "no injected IRC lines may be sent");
        Assert.IsFalse(feed.Sent.Any(s => s.Contains("\r\n", StringComparison.Ordinal) && !s.EndsWith("\r\n", StringComparison.Ordinal)),
            "no sent line may carry an embedded CRLF beyond its own terminator");

        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task Start_OverLengthChannelName_FallsBackToTwitch()
    {
        // Twitch channel names are capped at 25 chars - an over-cap name must
        // fall back to the default channel instead of JOINing a bogus target.
        var feed = new FakeFeed();
        var connection = new TwitchChatConnection(() => feed, () => 30);
        connection.Start(new string('a', 30));

        await TestWait.WaitUntilAsync(() => feed.Sent.Any(s => s.StartsWith("JOIN", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));

        Assert.IsTrue(feed.Sent.Contains("JOIN #twitch\r\n"), "an over-length channel must fall back to the default channel");

        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task Start_AfterConnectFault_ReconnectsAndRecovers()
    {
        var feed = new FakeFeed { ConnectError = new IOException("socket fault") };
        var connection = new TwitchChatConnection(() => feed, () => 30);
        connection.Start("test");

        // The first connect attempt faults; clear the fault so the next
        // attempt (after the 1s backoff) connects and processes messages.
        await TestWait.WaitUntilAsync(() => feed.ConnectCount >= 1, TimeSpan.FromSeconds(3));
        feed.ConnectError = null;
        feed.QueueMessage(":tmi.twitch.tv ROOMSTATE #test\r\n");
        feed.QueueMessage(":user!user@user.tmi.twitch.tv PRIVMSG #test :recovered\r\n");
        await TestWait.WaitUntilAsync(() => connection.MessageCount >= 1, TimeSpan.FromSeconds(8));

        Assert.IsTrue(feed.ConnectCount >= 2, "A failed connect must trigger a reconnect attempt");
        Assert.AreEqual(1, connection.MessageCount);

        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task PingFromServer_EchoesPongWithTheSamePayload()
    {
        var feed = new FakeFeed();
        feed.QueueMessage("PING :tmi.twitch.tv\r\n");
        var connection = new TwitchChatConnection(() => feed, () => 30);
        connection.Start("test");

        // The PONG is the fire-and-forget task that holds the PONG token (the
        // token RetirePongToken's deferral protects), so wait for it through
        // the seam instead of assuming the dispatch is synchronous.
        await TestWait.WaitUntilAsync(
            () => feed.Sent.Contains("PONG :tmi.twitch.tv\r\n"),
            TimeSpan.FromSeconds(3));

        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task Notice_LoginFailed_SetsTheLoginFailedState()
    {
        var feed = new FakeFeed();
        feed.QueueMessage(":tmi.twitch.tv NOTICE #test :Login authentication failed, please try again.\r\n");
        var (connection, states) = CreateRecordingConnection(feed, logError: (msg, _) => _loggedErrors.Enqueue(msg));
        connection.Start("test");

        // The login-failed state is transient: the queue drains right after
        // the NOTICE and the cycle ends into the reconnect backoff. The
        // recorded states capture the transition deterministically.
        await WaitUntilStateAsync(states, TwitchChatPresentation.ChatState.LoginFailed(), TimeSpan.FromSeconds(3));

        Assert.IsTrue(_loggedErrors.TryDequeue(out string? error)
            && error == "Twitch login failed: Login authentication failed, please try again.",
            "the login failure must log through the error seam");

        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task Notice_NotLoggedIn_SetsTheLiveState()
    {
        var feed = new FakeFeed();
        feed.QueueMessage(":tmi.twitch.tv NOTICE #test :you are not logged in\r\n");
        var (connection, states) = CreateRecordingConnection(feed);
        connection.Start("test");

        // The "not logged in" notice means the anonymous session is live; the
        // Live state is transient (the queue drains into the reconnect
        // backoff), so it is asserted from the recorded states.
        await WaitUntilStateAsync(states, TwitchChatPresentation.ChatState.Live(), TimeSpan.FromSeconds(3));

        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task CycleEnd_SetsTheReconnectingState()
    {
        var feed = new FakeFeed();
        var connection = new TwitchChatConnection(() => feed, () => 30);
        connection.Start("test");

        // The queue is empty, so the first cycle ends on a closed receive and
        // the reconnect backoff runs; the reconnecting state must surface.
        bool sawReconnecting = false;
        await TestWait.WaitUntilAsync(() =>
        {
            if (connection.State == TwitchChatPresentation.ChatState.Reconnecting()) sawReconnecting = true;
            return sawReconnecting;
        }, TimeSpan.FromSeconds(4));

        Assert.IsTrue(sawReconnecting, "a dropped cycle must surface the reconnecting state");

        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task Stop_SetsDisconnectedAndStopsTheLoop()
    {
        var feed = new FakeFeed();
        var connection = new TwitchChatConnection(() => feed, () => 30, () => true);
        connection.Start("test");
        await TestWait.WaitUntilAsync(() => feed.Sent.Any(s => s.StartsWith("JOIN", StringComparison.Ordinal)), TimeSpan.FromSeconds(3));

        connection.Stop();

        Assert.AreEqual(TwitchChatPresentation.ChatState.Disconnected(), connection.State);
        int connectsBeforeWait = feed.ConnectCount;
        await Task.Delay(1500); // past the 1s backoff: a live loop would have reconnected
        Assert.AreEqual(connectsBeforeWait, feed.ConnectCount, "a stopped connection must not reconnect");

        await connection.DisposeAsync();
    }

    [TestMethod]
    public async Task Buffer_HoldsAtMostTheClampedBound()
    {
        var feed = new FakeFeed();
        var connection = new TwitchChatConnection(() => feed, () => 10);
        connection.Start("test");
        for (int i = 0; i < 15; i++)
        {
            // Real Twitch PRIVMSGs carry the IRCv3 tags the parser reads the
            // username from; an untagged line would fall back to "user".
            feed.QueueMessage($"@display-name=u{i};login=u{i} :u{i}!u{i}@u{i}.tmi.twitch.tv PRIVMSG #test :msg {i}\r\n");
        }

        await TestWait.WaitUntilAsync(
            () => connection.MessageCount == 10 && connection.Messages[^1].Text == "msg 14",
            TimeSpan.FromSeconds(3));
        // The bound alone cannot distinguish "trimmed to 10" from "only 10
        // arrived": the tail pin above proves all 15 were received and the
        // oldest five trimmed.
        var messages = connection.Messages;
        Assert.AreEqual(10, connection.MessageCount, "the buffer must hold at most the bound");
        Assert.AreEqual("msg 14", messages[^1].Text, "the newest line must be the last");
        Assert.AreEqual("msg 5", messages[0].Text, "the oldest lines must be trimmed");
        Assert.AreEqual("u14", messages[^1].Username);
    }

    [TestMethod]
    public async Task Restart_ClearsThePreviousBufferAndJoinsTheNewChannel()
    {
        var feed = new FakeFeed();
        var connection = new TwitchChatConnection(() => feed, () => 30);
        connection.Start("test");
        feed.QueueMessage(":u!u@u.tmi.twitch.tv PRIVMSG #test :one\r\n");
        await TestWait.WaitUntilAsync(() => connection.MessageCount >= 1, TimeSpan.FromSeconds(3));

        connection.Start("other");

        Assert.AreEqual(0, connection.MessageCount, "a restart must clear the previous buffer");
        // The new loop's handshake is async: the old loop's bounded teardown
        // finishes inside Start, then the new cycle connects and sends the
        // JOIN, so the JOIN is awaited, not read immediately.
        await TestWait.WaitUntilAsync(() => feed.Sent.Contains("JOIN #other\r\n"), TimeSpan.FromSeconds(3));
        Assert.IsTrue(feed.Sent.Contains("JOIN #other\r\n"), "the restart must join the new channel");

        await connection.DisposeAsync();
    }

    [TestMethod]
    public void NormalizeChannel_TrimLowercasesAndDropsTheHash()
    {
        Assert.AreEqual("twitch", TwitchIrcMessages.NormalizeChannel("  #Twitch  "));
        Assert.AreEqual("mychannel", TwitchIrcMessages.NormalizeChannel("MyChannel"));
        Assert.AreEqual(TwitchIrcMessages.DefaultChannel, TwitchIrcMessages.NormalizeChannel(""));
        Assert.AreEqual(TwitchIrcMessages.DefaultChannel, TwitchIrcMessages.NormalizeChannel("#"));
        Assert.AreEqual(TwitchIrcMessages.DefaultChannel, TwitchIrcMessages.NormalizeChannel(new string('a', 26)), "over the 25-char cap");
        Assert.AreEqual(TwitchIrcMessages.DefaultChannel, TwitchIrcMessages.NormalizeChannel("bad\r\nname"), "embedded CRLF");
    }

    /// <summary>
    /// Creates a connection that records every state it transitions to,
    /// through the module's onChanged hook (fired on the loop thread right
    /// after each SetState and each buffer append). The feed's queue drains
    /// faster than a poll can sample, so the Live / LoginFailed states exist
    /// only briefly before the cycle ends into the reconnect backoff; the
    /// recorder captures the transitions, so a test can assert a transient
    /// state was reached instead of polling a value that has already moved on.
    /// </summary>
    private static (TwitchChatConnection Connection, System.Collections.Concurrent.ConcurrentBag<TwitchChatPresentation.ChatState> States)
        CreateRecordingConnection(FakeFeed feed, Action<string, Exception?>? logError = null)
    {
        var states = new System.Collections.Concurrent.ConcurrentBag<TwitchChatPresentation.ChatState>();
        TwitchChatConnection connection = null!;
        connection = new TwitchChatConnection(
            () => feed, () => 30, logError: logError, onChanged: () => states.Add(connection.State));
        return (connection, states);
    }

    /// <summary>Waits until the recorded states contain the target transition
    /// (the wait itself is the assertion: it fails on timeout).</summary>
    private static Task WaitUntilStateAsync(
        System.Collections.Concurrent.ConcurrentBag<TwitchChatPresentation.ChatState> states,
        TwitchChatPresentation.ChatState target, TimeSpan timeout)
        => TestWait.WaitUntilAsync(() => states.Contains(target), timeout);
}
