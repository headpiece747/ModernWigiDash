using System.IO;
using ModernWigiDash.Sdk;
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
    private sealed class FakeFeed : IWebSocketFeed
    {
        private readonly Queue<string> _incoming = new();
        public List<string> Sent { get; } = [];
        public bool IsOpen { get; set; } = true;
        public int ConnectCount { get; private set; }
        public Exception? ConnectError { get; set; }

        public void QueueMessage(string message) => _incoming.Enqueue(message);

        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            ConnectCount++;
            return ConnectError is null ? Task.CompletedTask : Task.FromException(ConnectError);
        }

        public Task SendTextAsync(string payload, CancellationToken ct)
        {
            Sent.Add(payload);
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveTextAsync(CancellationToken ct)
            => Task.FromResult(_incoming.Count > 0 ? _incoming.Dequeue() : null);

        public void Abort() => IsOpen = false;
        public void Dispose() { }
    }

    private sealed class FakeContext : IModernWigiDashContext
    {
        public void RequestRender() { }
        public void RequestInspectorRefresh() { }
        public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt) { }
        public void CloseDeviceAuthorization() { }
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }
        Assert.IsTrue(condition(), "Condition was not met within timeout");
    }

    [TestMethod]
    public async Task IrcLoop_SendsHandshakeAndParsesPrivmsg()
    {
        var feed = new FakeFeed();
        feed.QueueMessage(":tmi.twitch.tv ROOMSTATE #test\r\n");
        feed.QueueMessage(":user!user@user.tmi.twitch.tv PRIVMSG #test :hello world\r\n");
        var widget = new TwitchChatStreamWidget { AutoConnect = true, ChannelName = "test" };
        widget.FeedFactory = () => feed;
        await widget.InitializeAsync(new FakeContext(), CancellationToken.None);

        await WaitUntilAsync(() => widget.MessageCountForTest >= 1);

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
        await widget.InitializeAsync(new FakeContext(), CancellationToken.None);

        // The first connect attempt faults; clear the fault so the next
        // attempt (after the 2s backoff) connects and processes messages.
        await WaitUntilAsync(() => feed.ConnectCount >= 1);
        feed.ConnectError = null;
        feed.QueueMessage(":tmi.twitch.tv ROOMSTATE #test\r\n");
        feed.QueueMessage(":user!user@user.tmi.twitch.tv PRIVMSG #test :recovered\r\n");
        await WaitUntilAsync(() => widget.MessageCountForTest >= 1, timeoutMs: 8000);

        Assert.IsTrue(feed.ConnectCount >= 2, "A failed connect must trigger a reconnect attempt");
        Assert.AreEqual(1, widget.MessageCountForTest);

        await widget.DisposeAsync();
    }
}
