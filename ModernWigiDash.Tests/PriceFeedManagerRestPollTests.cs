using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The REST poll bodies of <see cref="PriceFeedManager"/>: one fetch → parse →
/// store hop per symbol, driven through the injectable HttpClient (the 30s
/// delay-first loop cadence is not practically testable, so the bodies are the
/// seam — the wire parsers they call are pinned separately in the messages
/// tests).
/// </summary>
[TestClass]
public class PriceFeedManagerRestPollTests
{
    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [TestMethod]
    public async Task PollStockSymbolAsync_FinnhubQuote_StoresPrice()
    {
        var stub = new StubHttpHandler(_ => Ok("""{"c":150.5,"d":2.1,"dp":1.4,"h":152,"l":148,"o":148.5,"pc":148.5}"""));
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.PollStockSymbolAsync("AAPL");

        var info = feed.GetPrice("AAPL", AssetKind.Stock);
        Assert.IsNotNull(info);
        Assert.AreEqual(150.5m, info!.Price);
        Assert.AreEqual(1.4m, info.ChangePercent);
        Assert.AreEqual("Finnhub", info.Source);
        StringAssert.Contains(stub.RequestUrls[0], "token=test-key");
    }

    [TestMethod]
    public async Task PollStockSymbolAsync_InvalidSymbol_MakesNoRequest()
    {
        var stub = new StubHttpHandler("{}");
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.PollStockSymbolAsync("AAPL&x=1");

        Assert.AreEqual(0, stub.Calls);
    }

    [TestMethod]
    public async Task PollFxPairAsync_FrankfurterSeries_StoresPriceAndChange()
    {
        var stub = new StubHttpHandler(_ => Ok("""{"rates":{"2025-08-01":{"EUR":0.95},"2025-08-02":{"EUR":0.93}}}"""));
        using var feed = new PriceFeedManager(new HttpClient(stub));

        await feed.PollFxPairAsync("USDEUR");

        var info = feed.GetPrice("USDEUR", AssetKind.Fx);
        Assert.IsNotNull(info);
        Assert.AreEqual(0.93m, info!.Price);
        Assert.AreEqual((0.93m / 0.95m - 1m) * 100m, info.ChangePercent);
        Assert.AreEqual("Frankfurter", info.Source);
        // The date window is Clock-driven: today and today-10d.
        StringAssert.Matches(stub.RequestUrls[0], new Regex(@"^https://api\.frankfurter\.app/\d{4}-\d{2}-\d{2}\.\.\d{4}-\d{2}-\d{2}\?from=USD&to=EUR$"));
    }

    [TestMethod]
    public async Task PollFxPairAsync_InvalidKey_MakesNoRequest()
    {
        var stub = new StubHttpHandler("{}");
        using var feed = new PriceFeedManager(new HttpClient(stub));

        await feed.PollFxPairAsync("USDEURX");

        Assert.AreEqual(0, stub.Calls);
    }

    [TestMethod]
    public async Task FallbackCoinGeckoAsync_SubscribedAlias_StoresPrice()
    {
        var stub = new StubHttpHandler(_ => Ok("""{"bitcoin":{"usd":65000,"usd_24h_change":2.5}}"""));
        using var feed = new PriceFeedManager(new HttpClient(stub), feedFactory: _ => new FakeFeed());
        feed.Subscribe("bitcoin", AssetKind.Crypto);

        await feed.FallbackCoinGeckoAsync();

        var info = feed.GetPrice("BTC", AssetKind.Crypto);
        Assert.IsNotNull(info);
        Assert.AreEqual(65000m, info!.Price);
        Assert.AreEqual(2.5m, info.ChangePercent);
        Assert.AreEqual("CoinGecko", info.Source);
        StringAssert.Contains(stub.RequestUrls[0], "ids=bitcoin");
    }
}
