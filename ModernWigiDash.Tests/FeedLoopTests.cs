namespace ModernWigiDash.Tests;

/// <summary>The reconnect policies behind the WebSocket feed loops — one place
/// where the fixed and exponential backoff behavior is pinned.</summary>
[TestClass]
public class FeedLoopTests
{
    [TestMethod]
    public async Task Start_WhenAlreadyStarted_DoesNotStartSecondLoop()
    {
        // Regression guard for duplicate sockets: PriceFeedManager calls
        // Start() per subscribed symbol; a second Start on a running loop must
        // not launch a second connect attempt. The parked connect holds the
        // first loop inside ConnectAsync while the assertion runs.
        var feed = new FakeFeed { ParkConnect = true };
        var loop = new FeedLoop(
            new Uri("wss://example.test"),
            () => feed,
            (f, ct) => Task.CompletedTask,
            _ => { },
            new FixedReconnectPolicy(TimeSpan.FromMilliseconds(10)));

        loop.Start();
        loop.Start();

        // The first loop must reach the parked connect before we assert.
        await TestWait.WaitUntilAsync(() => feed.ConnectCount >= 1, TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.AreEqual(1, feed.ConnectCount, "A second Start must not begin a second connection");

        loop.Dispose();
        loop.Dispose(); // idempotent — must not throw
    }

    [TestMethod]
    public void FixedPolicy_ReturnsConstantDelay()
    {
        var policy = new FixedReconnectPolicy(TimeSpan.FromSeconds(5));

        Assert.AreEqual(TimeSpan.FromSeconds(5), policy.NextDelay(faulted: true));
        Assert.AreEqual(TimeSpan.FromSeconds(5), policy.NextDelay(faulted: false));
    }

    [TestMethod]
    public void ExponentialPolicy_EscalatesOnFault_ResetsOnHealthyCycle()
    {
        var policy = new ExponentialBackoffReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        Assert.AreEqual(TimeSpan.FromSeconds(2), policy.NextDelay(faulted: true));
        Assert.AreEqual(TimeSpan.FromSeconds(4), policy.NextDelay(faulted: true));
        Assert.AreEqual(TimeSpan.FromSeconds(1), policy.NextDelay(faulted: false), "A healthy cycle must reset the backoff");
        Assert.AreEqual(TimeSpan.FromSeconds(2), policy.NextDelay(faulted: true), "The reset must not poison later escalation");
    }

    [TestMethod]
    public void ExponentialPolicy_CapsAtMaxDelay()
    {
        var policy = new ExponentialBackoffReconnectPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        for (int i = 0; i < 5; i++)
        {
            policy.NextDelay(faulted: true);
        }

        Assert.AreEqual(TimeSpan.FromSeconds(5), policy.NextDelay(faulted: true), "The backoff must never exceed the cap");
    }
}
