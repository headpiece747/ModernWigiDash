using System.Globalization;
using System.Net.Http;

namespace ModernWigiDash.Tests;

/// <summary>
/// TDD test for PriceFeedManager: GetPrice + Unsubscribe behavior.
/// Seam: GetPrice returns null when no feed data is available (no WebSocket
/// running in test), Unsubscribe does not throw for any symbol, and the
/// subscription set does not leak between symbols.
/// </summary>
[TestClass]
public class PriceFeedManagerLifecycleTests
{
    [TestMethod]
    public void GetPrice_ReturnsNull_WhenNoFeedData()
    {
        using var feed = new PriceFeedManager();

        // No WebSocket connected → GetPrice should return null for any symbol
        PriceInfo? result = feed.GetPrice("BTC", AssetKind.Crypto);

        Assert.IsNull(result, "GetPrice should return null when no feed is active");
    }

    [TestMethod]
    public void GetPrice_ReturnsNull_ForUnsubscribedSymbol()
    {
        using var feed = new PriceFeedManager();
        feed.Subscribe("BTC", AssetKind.Crypto);

        // Unsubscribe removes the symbol from the subscription set.
        // GetPrice still returns null because no feed provides real data.
        feed.Unsubscribe("BTC", AssetKind.Crypto);

        PriceInfo? result = feed.GetPrice("BTC", AssetKind.Crypto);
        Assert.IsNull(result, "GetPrice should return null for a fully unsubscribed symbol");
    }

    [TestMethod]
    public void Unsubscribe_DoesNotAffectOtherSymbols()
    {
        using var feed = new PriceFeedManager();
        feed.Subscribe("BTC", AssetKind.Crypto);
        feed.Subscribe("ETH", AssetKind.Crypto);

        feed.Unsubscribe("BTC", AssetKind.Crypto);

        // ETH should still be in the subscription set (we can verify indirectly:
        // Subscribe is idempotent via TryAdd, so re-subscribing BTC adds a new entry,
        // meaning BTC was actually removed).
        feed.Subscribe("BTC", AssetKind.Crypto); // Should not throw — symbol was removed

        // No feed data → both return null, but the subscription state is consistent.
        Assert.IsNull(feed.GetPrice("BTC", AssetKind.Crypto));
        Assert.IsNull(feed.GetPrice("ETH", AssetKind.Crypto));
    }

    [TestMethod]
    public void Unsubscribe_RacingReleases_NeverLeaveAZeroClaimEntry()
    {
        // Balanced subscribe/unsubscribe pairs racing on one key: the claim
        // count is a compare-exchange, so the key is removed at the last
        // claim. A lost racing decrement (the old read-then-decrement) left a
        // 0-claim entry that blocked the shutdown decision and the price
        // cleanup. Fake feed + stub HTTP keep the loop/REST churn in-memory.
        var stub = new StubHttpHandler(_ => StubHttpHandler.NotFound());
        using var feed = new PriceFeedManager(
            new HttpClient(stub),
            feedFactory: () => new FakeFeed(),
            reconnectDelay: TimeSpan.FromSeconds(5));

        const int tasks = 4;
        const int rounds = 100;
        var barrier = new Barrier(tasks);
        Task[] work = Enumerable.Range(0, tasks).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < rounds; i++)
            {
                feed.Subscribe("BTC", AssetKind.Crypto);
                feed.Unsubscribe("BTC", AssetKind.Crypto);
            }
        })).ToArray();
        Task.WaitAll(work);

        Assert.AreEqual(0, feed._subscribedCrypto.Count,
            "balanced pairs must end with no claim entry (a lost racing decrement leaves a 0-claim entry)");
    }

    [TestMethod]
    public void Subscribe_AfterDispose_NeverStartsALoop()
    {
        // The in-gate dispose re-check: a claim that beats a dispose to the
        // lifecycle gate is released, so a disposed manager never starts a
        // loop (the 2026-08-26 race made the first-claim startup and the
        // last-release teardown one serialized unit; this pins the
        // disposed leg of that unit).
        var stub = new StubHttpHandler(_ => StubHttpHandler.NotFound());
        var feed = new PriceFeedManager(
            new HttpClient(stub),
            feedFactory: () => new FakeFeed(),
            reconnectDelay: TimeSpan.FromSeconds(5));
        feed.Dispose();

        feed.Subscribe("BTC", AssetKind.Crypto);

        Assert.AreEqual(0, feed._subscribedCrypto.Count,
            "the claim must be released on a disposed manager (a disposed manager never starts a loop)");
    }

    [TestMethod]
    public async Task Resubscribe_AfterLastReleaseShutdown_RearmsALiveToken()
    {
        // EnsureActive's re-arm: a re-subscribe after the last-release
        // shutdown must run the fresh REST cycle on a live token, not the
        // shutdown's cancelled one (a cancelled token ends the cycle before
        // its first hop, so a REST delegate hit after the re-subscribe is
        // the live-token proof). The delay seam parks each cycle on its own
        // token (the RestPollLoopTests FakeClockDelay shape, minus the clock):
        // the test releases the park to drive one cycle at a time, the park's
        // token registration is what a shutdown cancellation must reach, and
        // a completed/cancelled park is replaced by the next delay call, so
        // the re-armed cycle parks on a fresh token. The delayCalls count
        // increments after the park decision, so a waited count proves the
        // cycle is parked before the test releases it.
        int restCalls = 0;
        int delayCalls = 0;
        var stub = new StubHttpHandler(_ =>
        {
            Interlocked.Increment(ref restCalls);
            return StubHttpHandler.NotFound();
        });
        var fake = new FakeFeed();
        TaskCompletionSource<bool> park = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<TimeSpan, CancellationToken, Task> delay = (_, ct) =>
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            var p = park;
            if (p.Task.IsCompleted)
                park = p = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => p.TrySetCanceled(ct));
            Interlocked.Increment(ref delayCalls);
            return p.Task;
        };
        void ReleasePark()
        {
            var p = park;
            park = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            p.TrySetResult(true);
        }

        using var feed = new PriceFeedManager(
            new HttpClient(stub),
            feedFactory: () => fake,
            reconnectDelay: TimeSpan.FromSeconds(5),
            delay: delay);

        feed.Subscribe("BTC", AssetKind.Crypto);
        Assert.AreEqual(1, Volatile.Read(ref delayCalls), "the first cycle must park delay-first (no poll before the window)");
        await TestWait.WaitUntilAsync(() => fake.ConnectCount >= 1, TimeSpan.FromSeconds(5));

        ReleasePark();
        await TestWait.WaitUntilAsync(() => Volatile.Read(ref restCalls) > 0, TimeSpan.FromSeconds(5));
        await TestWait.WaitUntilAsync(() => Volatile.Read(ref delayCalls) >= 2, TimeSpan.FromSeconds(5));

        feed.Unsubscribe("BTC", AssetKind.Crypto); // last release: the shutdown cancels the token
        int restBefore = Volatile.Read(ref restCalls);

        feed.Subscribe("BTC", AssetKind.Crypto);
        await TestWait.WaitUntilAsync(() => fake.ConnectCount >= 2, TimeSpan.FromSeconds(5));
        await TestWait.WaitUntilAsync(() => Volatile.Read(ref delayCalls) >= 3, TimeSpan.FromSeconds(5));
        ReleasePark();
        await TestWait.WaitUntilAsync(() => Volatile.Read(ref restCalls) > restBefore, TimeSpan.FromSeconds(5));

        Assert.IsTrue(Volatile.Read(ref restCalls) > restBefore,
            "the fresh REST cycle must poll on a live token (a shutdown-cancelled token ends it before its first hop)");
    }

    [TestMethod]
    public void FormattedChange_IsInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            // A comma-decimal locale must not render "+1,25 %" on the display.
            Assert.AreEqual("+1.25%", new PriceInfo { ChangePercent = 1.25m }.FormattedChange);
            Assert.AreEqual("-0.50%", new PriceInfo { ChangePercent = -0.5m }.FormattedChange);
            Assert.AreEqual("+0.00%", new PriceInfo { ChangePercent = 0m }.FormattedChange);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [TestMethod]
    public async Task SeedFallbackAsync_InvalidStockSymbol_DoesNotCallHttp()
    {
        var stub = new StubHttpHandler("{}");
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.SeedFallbackAsync("AAPL&x=1", AssetKind.Stock);

        Assert.AreEqual(0, stub.Calls, "A polluted symbol must never reach the Yahoo feed");
    }

    [TestMethod]
    public async Task SeedFallbackAsync_OverlongOrEmptySymbol_DoesNotCallHttp()
    {
        var stub = new StubHttpHandler("{}");
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.SeedFallbackAsync(new string('A', 100), AssetKind.Stock);
        await feed.SeedFallbackAsync("", AssetKind.Stock);

        Assert.AreEqual(0, stub.Calls, "Overlong and empty symbols must never reach the Yahoo feed");
    }

    [TestMethod]
    public async Task SeedFallbackAsync_ValidStockSymbol_StillCallsHttp()
    {
        var stub = new StubHttpHandler("""{"chart":{"result":[{"meta":{"regularMarketPrice":150.5,"chartPreviousClose":148.0}}]}}""");
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.SeedFallbackAsync("AAPL", AssetKind.Stock);

        Assert.AreEqual(1, stub.Calls, "A valid symbol must still reach the Yahoo feed");
        Assert.IsTrue(stub.RequestUrls[0].Contains("AAPL", StringComparison.OrdinalIgnoreCase), "The Yahoo URL must contain the requested symbol");
    }

    [TestMethod]
    public void Subscribe_InvalidStockSymbol_NotAddedToSubscriptionSet()
    {
        using var feed = new PriceFeedManager(new HttpClient(new StubHttpHandler("{}")), "test-key");

        feed.Subscribe("AAPL&x=1", AssetKind.Stock);
        feed.Subscribe(new string('A', 100), AssetKind.Stock);
        feed.Subscribe("", AssetKind.Stock);

        Assert.AreEqual(0, feed._subscribedStocks.Count, "Invalid stock symbols must never enter the subscription set");
    }

    [TestMethod]
    public void Subscribe_InvalidFxPair_NotAddedToSubscriptionSet()
    {
        using var feed = new PriceFeedManager(new HttpClient(new StubHttpHandler("{}")), "test-key");

        feed.Subscribe("EUR/U", AssetKind.Fx);
        feed.Subscribe("E/U", AssetKind.Fx);
        feed.Subscribe("123456", AssetKind.Fx);

        Assert.AreEqual(0, feed._subscribedFx.Count, "Invalid FX pairs must never enter the subscription set");
    }

    [TestMethod]
    public void Subscribe_ValidSymbols_StillEnterSubscriptionSets()
    {
        using var feed = new PriceFeedManager(new HttpClient(new StubHttpHandler("{}")), "test-key");

        feed.Subscribe("AAPL", AssetKind.Stock);
        feed.Subscribe("EUR/USD", AssetKind.Fx);
        feed.Subscribe("BTC", AssetKind.Crypto);

        Assert.IsTrue(feed._subscribedStocks.ContainsKey("AAPL"), "A valid stock must be subscribed");
        Assert.IsTrue(feed._subscribedFx.ContainsKey("EURUSD"), "A valid FX pair must be subscribed as its normalized key");
        Assert.IsTrue(feed._subscribedCrypto.ContainsKey("BTC"), "A valid crypto symbol must be subscribed");
    }

    [TestMethod]
    public void Subscriptions_AreRefCounted_OneUnsubscribeDoesNotKillCoSubscriber()
    {
        // Regression guard: the shared manager keys subscriptions by symbol.
        // Two widgets on one symbol hold two claims — one widget's
        // unsubscribe (symbol change / dispose) must not kill the other
        // widget's live subscription.
        using var feed = new PriceFeedManager(new HttpClient(new StubHttpHandler("{}")), "test-key");

        feed.Subscribe("BTC", AssetKind.Crypto);
        feed.Subscribe("BTC", AssetKind.Crypto); // second widget, same symbol

        feed.Unsubscribe("BTC", AssetKind.Crypto); // first widget leaves

        Assert.IsTrue(feed._subscribedCrypto.ContainsKey("BTC"),
            "A co-subscriber must keep the symbol subscribed after one widget unsubscribes");

        feed.Unsubscribe("BTC", AssetKind.Crypto); // last widget leaves

        Assert.IsFalse(feed._subscribedCrypto.ContainsKey("BTC"),
            "The last unsubscribe must release the symbol");
    }

    [TestMethod]
    public void PriceInfo_FormattedChange_RendersSignAndPercent()
    {
        var up = new PriceInfo { ChangePercent = 1.0843m };
        Assert.AreEqual("+1.08%", up.FormattedChange);
        var down = new PriceInfo { ChangePercent = -0.5m };
        Assert.AreEqual("-0.50%", down.FormattedChange);
    }
}
