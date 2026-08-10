using System.Net.Http;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The ticker's feed-identity ownership: diff against the last tracked
/// identity, release the old claim, subscribe the new one, seed the fallback
/// fetch — without a widget instance.
/// </summary>
[TestClass]
public class FeedSubscriptionTests
{
    private static PriceFeedManager CreateOfflineFeed() => new(
        new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())),
        "test-key",
        feedFactory: _ => new FakeFeed(),
        reconnectDelay: TimeSpan.FromMilliseconds(10));

    [TestMethod]
    public void Track_NewIdentity_SubscribesAndSeedsFallback()
    {
        using var feed = CreateOfflineFeed();
        int seeds = 0;
        var subscription = new FeedSubscription(() => seeds++);

        subscription.Track("BTC", AssetKind.Crypto, feed);

        Assert.IsTrue(feed._subscribedCrypto.ContainsKey("BTC"), "A new identity must subscribe");
        Assert.AreEqual(1, seeds, "A new identity must seed the fallback fetch");
    }

    [TestMethod]
    public void Track_SameIdentity_NoOp()
    {
        using var feed = CreateOfflineFeed();
        int seeds = 0;
        var subscription = new FeedSubscription(() => seeds++);

        subscription.Track("BTC", AssetKind.Crypto, feed);
        subscription.Track("BTC", AssetKind.Crypto, feed);

        Assert.AreEqual(1, feed._subscribedCrypto["BTC"], "An unchanged identity must not re-claim");
        Assert.AreEqual(1, seeds, "An unchanged identity must not re-seed");
    }

    [TestMethod]
    public void Track_IdentityChange_UnsubscribesOldSubscribesNew()
    {
        using var feed = CreateOfflineFeed();
        int seeds = 0;
        var subscription = new FeedSubscription(() => seeds++);

        subscription.Track("BTC", AssetKind.Crypto, feed);
        subscription.Track("ETH", AssetKind.Crypto, feed);

        Assert.IsTrue(feed._subscribedCrypto.ContainsKey("ETH"), "A symbol change must subscribe the new symbol");
        Assert.IsFalse(feed._subscribedCrypto.ContainsKey("BTC"), "A symbol change must unsubscribe the old symbol");
        Assert.AreEqual(2, seeds);
    }

    [TestMethod]
    public void Track_KindChangeWithSameSymbol_IsAnIdentityChange()
    {
        using var feed = CreateOfflineFeed();
        var subscription = new FeedSubscription(() => { });

        subscription.Track("AAPL", AssetKind.Stock, feed);
        subscription.Track("AAPL", AssetKind.Crypto, feed);

        Assert.IsTrue(feed._subscribedCrypto.ContainsKey("AAPL"), "The new kind must subscribe");
        Assert.IsFalse(feed._subscribedStocks.ContainsKey("AAPL"), "The old kind's claim must be released");
    }

    [TestMethod]
    public void Track_BlankSymbol_NeverSubscribes()
    {
        using var feed = CreateOfflineFeed();
        int seeds = 0;
        var subscription = new FeedSubscription(() => seeds++);

        subscription.Track("   ", AssetKind.Stock, feed);

        Assert.IsTrue(feed._subscribedCrypto.IsEmpty && feed._subscribedStocks.IsEmpty && feed._subscribedFx.IsEmpty,
            "A blank symbol must never subscribe");
        Assert.AreEqual(0, seeds, "A blank symbol must never seed");
    }

    [TestMethod]
    public void Track_BlankAfterSymbol_ReleasesOldClaim()
    {
        using var feed = CreateOfflineFeed();
        var subscription = new FeedSubscription(() => { });

        subscription.Track("BTC", AssetKind.Crypto, feed);
        subscription.Track("", AssetKind.Crypto, feed);

        Assert.IsTrue(feed._subscribedCrypto.IsEmpty, "Going blank must release the old claim");
    }

    [TestMethod]
    public void Untrack_ReleasesClaim()
    {
        using var feed = CreateOfflineFeed();
        var subscription = new FeedSubscription(() => { });
        subscription.Track("AAPL", AssetKind.Stock, feed);

        subscription.Untrack();

        Assert.IsTrue(feed._subscribedStocks.IsEmpty, "Untrack must release the subscription");
    }

    [TestMethod]
    public void Untrack_NeverTracked_IsNoOp()
    {
        using var feed = CreateOfflineFeed();
        var subscription = new FeedSubscription(() => { });

        subscription.Untrack();
        subscription.Untrack();

        Assert.IsTrue(feed._subscribedCrypto.IsEmpty && feed._subscribedStocks.IsEmpty && feed._subscribedFx.IsEmpty);
    }

    [TestMethod]
    public void Untrack_AfterBlank_IsNoOp()
    {
        using var feed = CreateOfflineFeed();
        var subscription = new FeedSubscription(() => { });
        subscription.Track("", AssetKind.Crypto, feed);

        subscription.Untrack();

        Assert.IsTrue(feed._subscribedCrypto.IsEmpty);
    }
}
