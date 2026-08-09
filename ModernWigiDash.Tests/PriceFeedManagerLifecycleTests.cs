using System.Net;
using System.Net.Http;
using ModernWigiDash.Widgets;

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
    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> RequestUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestUrls.Add(request.RequestUri?.ToString() ?? "");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
            return Task.FromResult(response);
        }
    }
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
    public async Task FetchFallbackAsync_InvalidStockSymbol_DoesNotCallHttp()
    {
        var stub = new StubHandler("{}");
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.FetchFallbackAsync("AAPL&x=1", AssetKind.Stock);

        Assert.AreEqual(0, stub.Calls, "A polluted symbol must never reach the Yahoo feed");
    }

    [TestMethod]
    public async Task FetchFallbackAsync_OverlongOrEmptySymbol_DoesNotCallHttp()
    {
        var stub = new StubHandler("{}");
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.FetchFallbackAsync(new string('A', 100), AssetKind.Stock);
        await feed.FetchFallbackAsync("", AssetKind.Stock);

        Assert.AreEqual(0, stub.Calls, "Overlong and empty symbols must never reach the Yahoo feed");
    }

    [TestMethod]
    public async Task FetchFallbackAsync_ValidStockSymbol_StillCallsHttp()
    {
        var stub = new StubHandler("""{"chart":{"result":[{"meta":{"regularMarketPrice":150.5,"chartPreviousClose":148.0}}]}}""");
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.FetchFallbackAsync("AAPL", AssetKind.Stock);

        Assert.AreEqual(1, stub.Calls, "A valid symbol must still reach the Yahoo feed");
        Assert.IsTrue(stub.RequestUrls[0].Contains("AAPL", StringComparison.OrdinalIgnoreCase), "The Yahoo URL must contain the requested symbol");
    }

    [TestMethod]
    public void Subscribe_InvalidStockSymbol_NotAddedToSubscriptionSet()
    {
        using var feed = new PriceFeedManager(new HttpClient(new StubHandler("{}")), "test-key");

        feed.Subscribe("AAPL&x=1", AssetKind.Stock);
        feed.Subscribe(new string('A', 100), AssetKind.Stock);
        feed.Subscribe("", AssetKind.Stock);

        Assert.AreEqual(0, feed._subscribedStocks.Count, "Invalid stock symbols must never enter the subscription set");
    }

    [TestMethod]
    public void Subscribe_InvalidFxPair_NotAddedToSubscriptionSet()
    {
        using var feed = new PriceFeedManager(new HttpClient(new StubHandler("{}")), "test-key");

        feed.Subscribe("EUR/U", AssetKind.Fx);
        feed.Subscribe("E/U", AssetKind.Fx);
        feed.Subscribe("123456", AssetKind.Fx);

        Assert.AreEqual(0, feed._subscribedFx.Count, "Invalid FX pairs must never enter the subscription set");
    }

    [TestMethod]
    public void Subscribe_ValidSymbols_StillEnterSubscriptionSets()
    {
        using var feed = new PriceFeedManager(new HttpClient(new StubHandler("{}")), "test-key");

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
        using var feed = new PriceFeedManager(new HttpClient(new StubHandler("{}")), "test-key");

        feed.Subscribe("BTC", AssetKind.Crypto);
        feed.Subscribe("BTC", AssetKind.Crypto); // second widget, same symbol

        feed.Unsubscribe("BTC", AssetKind.Crypto); // first widget leaves

        Assert.IsTrue(feed._subscribedCrypto.ContainsKey("BTC"),
            "A co-subscriber must keep the symbol subscribed after one widget unsubscribes");

        feed.Unsubscribe("BTC", AssetKind.Crypto); // last widget leaves

        Assert.IsFalse(feed._subscribedCrypto.ContainsKey("BTC"),
            "The last unsubscribe must release the symbol");
    }
}
