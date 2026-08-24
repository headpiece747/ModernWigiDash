using System.IO;
using System.Net.Http;

namespace ModernWigiDash.Tests;

/// <summary>
/// Tests the price-feed WebSocket seam: the Binance/Finnhub loops are driven by
/// an in-memory feed (no live socket) and the reconnect policy is exercised.
/// The crypto symbol table's CoinGecko invariant lives in SymbolCatalogTests.
/// </summary>
[TestClass]
public class PriceFeedSocketLoopTests
{
    [TestMethod]
    public async Task BinanceLoop_AppliesTickerFromFeed_UpdatesPrice()
    {
        var feed = new FakeFeed();
        using var manager = new PriceFeedManager(
            new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())),
            feedFactory: _ => feed,
            reconnectDelay: TimeSpan.FromMilliseconds(20));
        feed.QueueMessage("""{"e":"24hrTicker","s":"BTCUSDT","c":"65432.10","P":"1.23"}""");

        manager.Subscribe("BTC", AssetKind.Crypto);

        await TestWait.WaitUntilAsync(() => manager.GetPrice("BTC", AssetKind.Crypto) is not null, TimeSpan.FromSeconds(3));

        var price = manager.GetPrice("BTC", AssetKind.Crypto)!;
        Assert.AreEqual(65432.10m, price.Price);
        Assert.AreEqual(1.23m, price.ChangePercent);
        Assert.AreEqual("Binance", price.Source);
        Assert.IsTrue(feed.Sent.Count > 0, "The loop must send the connect-time subscription payload");
    }

    [TestMethod]
    public async Task BinanceLoop_ConnectTimeSubscribe_SendsTheExactWireFrame()
    {
        const string expectedFrame = """{"method":"SUBSCRIBE","params":["btcusdt@ticker"],"id":1}""";
        var feed = new FakeFeed();
        using var manager = new PriceFeedManager(
            new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())),
            feedFactory: _ => feed,
            reconnectDelay: TimeSpan.FromMilliseconds(20));

        manager.Subscribe("BTC", AssetKind.Crypto);

        // The connect-time payload is the wire frame the PriceFeedMessages
        // builder spells for every subscribed coin at connect time.
        await TestWait.WaitUntilAsync(
            () => feed.Sent.Count > 0 && feed.Sent[0] == expectedFrame,
            TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task BinanceLoop_SecondSymbol_SendsTheExactIncrementalWireFrame()
    {
        const string expectedBtcFrame = """{"method":"SUBSCRIBE","params":["btcusdt@ticker"],"id":1}""";
        const string expectedEthFrame = """{"method":"SUBSCRIBE","params":["ethusdt@ticker"],"id":1}""";
        var feed = new FakeFeed();
        using var manager = new PriceFeedManager(
            new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())),
            feedFactory: _ => feed,
            reconnectDelay: TimeSpan.FromMilliseconds(20));

        // A parked connect keeps the feed observed live (Current set, IsOpen)
        // without the reconnect churn an empty message queue would cause, so
        // the incremental send is the only frame that can cross the seam.
        feed.ParkConnect = true;
        manager.Subscribe("BTC", AssetKind.Crypto);
        await TestWait.WaitUntilAsync(() => feed.ConnectCount >= 1, TimeSpan.FromSeconds(3));

        // A symbol added after the socket connected gets its own subscribe
        // frame, the same builder spelling as the connect-time payload.
        manager.Subscribe("ETH", AssetKind.Crypto);

        await TestWait.WaitUntilAsync(
            () => feed.Sent.Count > 0 && feed.Sent[^1] == expectedEthFrame,
            TimeSpan.FromSeconds(3));
        foreach (var frame in feed.Sent)
        {
            Assert.IsTrue(frame is expectedBtcFrame or expectedEthFrame,
                $"every frame that crossed the seam is the builder's output, got: {frame}");
        }
    }

    [TestMethod]
    public async Task FinnhubLoop_ConnectTimeSubscribe_SendsOneExactWireFramePerSymbol()
    {
        const string expectedFrame = """{"type":"subscribe","symbol":"AAPL"}""";
        var feed = new FakeFeed();
        using var manager = new PriceFeedManager(
            new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())),
            finnhubApiKey: "test-key",
            feedFactory: _ => feed,
            reconnectDelay: TimeSpan.FromMilliseconds(20));

        manager.Subscribe("AAPL", AssetKind.Stock);

        // The Finnhub protocol is one frame per symbol; the builder is the
        // one spelling the connect-time payload uses.
        await TestWait.WaitUntilAsync(
            () => feed.Sent.Count > 0 && feed.Sent[0] == expectedFrame,
            TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task BinanceLoop_AfterConnectFault_Reconnects()
    {
        var feed = new FakeFeed();
        using var manager = new PriceFeedManager(
            new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())),
            feedFactory: _ => feed,
            reconnectDelay: TimeSpan.FromMilliseconds(20));

        manager.Subscribe("BTC", AssetKind.Crypto);

        // First connect attempt fails; the loop must try again after the delay.
        await TestWait.WaitUntilAsync(() => feed.ConnectCount >= 1, TimeSpan.FromSeconds(3));
        feed.ConnectError = new IOException("socket fault");
        await TestWait.WaitUntilAsync(() => feed.ConnectCount >= 2, TimeSpan.FromSeconds(3));
        Assert.IsTrue(feed.ConnectCount >= 2, "A failed connect must trigger a reconnect attempt");
    }

    [TestMethod]
    public async Task SeedFallbackAsync_CryptoWithKnownAlias_UsesCoinGeckoIdFromSingleTable()
    {
        var stub = new StubHttpHandler("""{"arbitrum":{"usd":1.05,"usd_24h_change":2.5}}""");
        using var manager = new PriceFeedManager(new HttpClient(stub), "test-key");

        // "arbitrum" is not the canonical symbol — the fallback must still
        // resolve its CoinGecko id through the single table (old two-table
        // layout silently dropped coins whose canonical symbol was not a key).
        await manager.SeedFallbackAsync("ARB", AssetKind.Crypto);

        Assert.AreEqual(1, stub.Calls);
        Assert.IsTrue(stub.RequestUrls[0].Contains("arbitrum", StringComparison.Ordinal), "Fallback URL must use the CoinGecko id");
        var price = manager.GetPrice("ARB", AssetKind.Crypto)!;
        Assert.AreEqual(1.05m, price.Price);
        Assert.AreEqual("CoinGecko", price.Source);
    }
}
