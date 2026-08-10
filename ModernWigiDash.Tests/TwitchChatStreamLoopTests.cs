using System.IO;
using ModernWigiDash.Widgets;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

/// <summary>
/// Drives the Twitch IRC loop through the <see cref="IWebSocketFeed"/> seam —
/// no network, no ClientWebSocket. The handshake payload, message parsing,
/// and reconnect backoff are exercised the same way PriceFeedSocketLoopTests
/// drives the price feeds.
/// </summary>
[TestClass]
public class TwitchChatStreamLoopTests
{
    /// <summary>A session with an empty token store: the widget's fire-and-forget
    /// session restore must never reach the real DPAPI store or the network from
    /// a test host (a valid stored token would mutate the shared session).</summary>
    private static TwitchSession EmptySession() =>
        new(
            new TwitchTokenStore(Path.Combine(Path.GetTempPath(), $"wmd-twitch-{Guid.NewGuid():N}.bin")),
            _ => throw new NotSupportedException("An empty store must never reach the API client"),
            TimeProvider.System);

    [TestMethod]
    public async Task IrcLoop_SendsHandshakeAndParsesPrivmsg()
    {
        var feed = new FakeFeed();
        feed.QueueMessage(":tmi.twitch.tv ROOMSTATE #test\r\n");
        feed.QueueMessage(":user!user@user.tmi.twitch.tv PRIVMSG #test :hello world\r\n");
        var widget = new TwitchChatStreamWidget { AutoConnect = true, ChannelName = "test" };
        widget.FeedFactory = () => feed;
        widget.Session = EmptySession();
        await widget.InitializeAsync(new TestContext(), CancellationToken.None);

        await TestWait.WaitUntilAsync(() => widget.MessageCountForTest >= 1, TimeSpan.FromSeconds(3));

        Assert.IsTrue(feed.Sent.Any(s => s.StartsWith("CAP REQ", StringComparison.Ordinal)), "CAP handshake must be sent");
        Assert.IsTrue(feed.Sent.Any(s => s.StartsWith("PASS ", StringComparison.Ordinal)), "PASS must be sent");
        Assert.IsTrue(feed.Sent.Any(s => s.StartsWith("NICK justinfan", StringComparison.Ordinal)), "Anonymous NICK must be sent");
        Assert.IsTrue(feed.Sent.Contains("JOIN #test\r\n"), "JOIN must target the normalized channel");
        Assert.AreEqual(1, widget.MessageCountForTest);

        await widget.DisposeAsync();
    }

    [TestMethod]
    public async Task IrcLoop_AfterConnectFault_ReconnectsAndRecovers()
    {
        var feed = new FakeFeed { ConnectError = new IOException("socket fault") };
        var widget = new TwitchChatStreamWidget { AutoConnect = true, ChannelName = "test" };
        widget.FeedFactory = () => feed;
        widget.Session = EmptySession();
        await widget.InitializeAsync(new TestContext(), CancellationToken.None);

        // The first connect attempt faults; clear the fault so the next
        // attempt (after the 2s backoff) connects and processes messages.
        await TestWait.WaitUntilAsync(() => feed.ConnectCount >= 1, TimeSpan.FromSeconds(3));
        feed.ConnectError = null;
        feed.QueueMessage(":tmi.twitch.tv ROOMSTATE #test\r\n");
        feed.QueueMessage(":user!user@user.tmi.twitch.tv PRIVMSG #test :recovered\r\n");
        await TestWait.WaitUntilAsync(() => widget.MessageCountForTest >= 1, TimeSpan.FromSeconds(8));

        Assert.IsTrue(feed.ConnectCount >= 2, "A failed connect must trigger a reconnect attempt");
        Assert.AreEqual(1, widget.MessageCountForTest);

        await widget.DisposeAsync();
    }
}
