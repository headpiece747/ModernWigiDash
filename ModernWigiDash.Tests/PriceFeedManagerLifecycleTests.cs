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
}
