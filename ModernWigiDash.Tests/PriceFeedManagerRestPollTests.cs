using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The REST quote legs behind <see cref="PriceFeedManager"/>: one fetch →
/// parse → store hop per symbol, driven through the injectable HttpClient.
/// The cycle cadence itself is the RestPollLoop module (pinned by
/// RestPollLoopTests through its injected delay delegate), and the wire
/// parsers the legs call are pinned separately in the messages tests.
/// </summary>
[TestClass]
public class PriceFeedManagerRestPollTests
{
    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [TestMethod]
    public async Task PollStockAsync_FinnhubQuote_StoresPrice()
    {
        var stub = new StubHttpHandler(_ => Ok("""{"c":150.5,"d":2.1,"dp":1.4,"h":152,"l":148,"o":148.5,"pc":148.5}"""));
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.PollStockAsync("AAPL");

        var info = feed.GetPrice("AAPL", AssetKind.Stock);
        Assert.IsNotNull(info);
        Assert.AreEqual(150.5m, info.Price);
        Assert.AreEqual(1.4m, info.ChangePercent);
        Assert.AreEqual("Finnhub", info.Source);
        StringAssert.Contains(stub.RequestUrls[0], "token=test-key");
    }

    [TestMethod]
    public async Task PollStockAsync_InvalidSymbol_MakesNoRequest()
    {
        var stub = new StubHttpHandler("{}");
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.PollStockAsync("AAPL&x=1");

        Assert.AreEqual(0, stub.Calls);
    }

    [TestMethod]
    public async Task PollFxAsync_FrankfurterSeries_StoresPriceAndChange()
    {
        var stub = new StubHttpHandler(_ => Ok("""{"rates":{"2025-08-01":{"EUR":0.95},"2025-08-02":{"EUR":0.93}}}"""));
        using var feed = new PriceFeedManager(new HttpClient(stub));

        await feed.PollFxAsync("USDEUR");

        var info = feed.GetPrice("USDEUR", AssetKind.Fx);
        Assert.IsNotNull(info);
        Assert.AreEqual(0.93m, info.Price);
        Assert.AreEqual((0.93m / 0.95m - 1m) * 100m, info.ChangePercent);
        Assert.AreEqual("Frankfurter", info.Source);
        // The date window is Clock-driven: today and today-10d.
        StringAssert.Matches(stub.RequestUrls[0], new Regex(@"^https://api\.frankfurter\.app/\d{4}-\d{2}-\d{2}\.\.\d{4}-\d{2}-\d{2}\?from=USD&to=EUR$"));
    }

    [TestMethod]
    public async Task PollFxAsync_InvalidKey_MakesNoRequest()
    {
        var stub = new StubHttpHandler("{}");
        using var feed = new PriceFeedManager(new HttpClient(stub));

        await feed.PollFxAsync("USDEURX");

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
        Assert.AreEqual(65000m, info.Price);
        Assert.AreEqual(2.5m, info.ChangePercent);
        Assert.AreEqual("CoinGecko", info.Source);
        StringAssert.Contains(stub.RequestUrls[0], "ids=bitcoin");
    }

    [TestMethod]
    public async Task PollCryptoAsync_BinanceUs24HrTicker_StoresPrice()
    {
        var stub = new StubHttpHandler(_ => Ok("""{"symbol":"BTCUSDT","lastPrice":"65000.0","priceChangePercent":"2.5"}"""));
        using var feed = new PriceFeedManager(new HttpClient(stub));

        await feed.PollCryptoAsync("BTC");

        var info = feed.GetPrice("BTC", AssetKind.Crypto);
        Assert.IsNotNull(info);
        Assert.AreEqual(65000m, info.Price);
        Assert.AreEqual(2.5m, info.ChangePercent);
        Assert.AreEqual("BinanceUS", info.Source);
        StringAssert.Contains(stub.RequestUrls[0], "https://api.binance.us/api/v3/ticker/24hr?symbol=BTCUSDT");
    }

    [TestMethod]
    public async Task PollCryptoAsync_UnparseableBody_StoresNothing()
    {
        var stub = new StubHttpHandler("{}");
        using var feed = new PriceFeedManager(new HttpClient(stub));

        await feed.PollCryptoAsync("BTC");

        Assert.IsNull(feed.GetPrice("BTC", AssetKind.Crypto));
    }

    // ── freshness policy: the 60s window, one spelling ─────────────────

    [TestMethod]
    public void PriceInfo_IsStale_WithinWindow_False()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var info = new PriceInfo { Price = 100m, Timestamp = clock.GetUtcNow().UtcDateTime, Clock = clock };

        Assert.IsFalse(info.IsStale, "a price stamped inside the 60s window is fresh");
    }

    [TestMethod]
    public void PriceInfo_IsStale_PastWindow_True()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var info = new PriceInfo { Price = 100m, Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-61), Clock = clock };

        Assert.IsTrue(info.IsStale, "a price stamped outside the 60s window is stale");
    }

    [TestMethod]
    public void ShouldKeepFreshBinanceUs_FreshBinanceUs_True()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var existing = new PriceInfo { Price = 100m, Source = PriceFeedManager.SourceBinanceUs, Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-30) };

        Assert.IsTrue(PriceFeedManager.ShouldKeepFreshBinanceUs(existing, clock.GetUtcNow().UtcDateTime),
            "a fresh BinanceUS price must not be downgraded by the CoinGecko fallback");
    }

    [TestMethod]
    public void ShouldKeepFreshBinanceUs_StaleBinanceUs_False()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var existing = new PriceInfo { Price = 100m, Source = PriceFeedManager.SourceBinanceUs, Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-61) };

        Assert.IsFalse(PriceFeedManager.ShouldKeepFreshBinanceUs(existing, clock.GetUtcNow().UtcDateTime),
            "a stale BinanceUS price may be replaced by the fallback");
    }

    [TestMethod]
    public void ShouldKeepFreshBinanceUs_OtherSource_False()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var existing = new PriceInfo { Price = 100m, Source = "CoinGecko", Timestamp = clock.GetUtcNow().UtcDateTime };

        Assert.IsFalse(PriceFeedManager.ShouldKeepFreshBinanceUs(existing, clock.GetUtcNow().UtcDateTime),
            "only BinanceUS prices are protected by the downgrade guard");
    }

    [TestMethod]
    public async Task FallbackCoinGeckoAsync_FreshBinanceUsPrice_IsNotDowngraded()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("binance.us", StringComparison.Ordinal)
                ? Ok("""{"symbol":"BTCUSDT","lastPrice":"65000.0","priceChangePercent":"2.5"}""")
                : Ok("""{"bitcoin":{"usd":60000.0,"usd_24h_change":1.5}}""");
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        using var feed = new PriceFeedManager(new HttpClient(stub), feedFactory: _ => new FakeFeed()) { Clock = clock };
        feed.Subscribe("bitcoin", AssetKind.Crypto);

        await feed.PollCryptoAsync("BTC");
        clock.Advance(TimeSpan.FromSeconds(30));
        await feed.FallbackCoinGeckoAsync();

        var info = feed.GetPrice("BTC", AssetKind.Crypto);
        Assert.IsNotNull(info);
        Assert.AreEqual(65000m, info.Price, "a fresh BinanceUS price must survive the CoinGecko fallback");
        Assert.AreEqual(PriceFeedManager.SourceBinanceUs, info.Source);
    }

    [TestMethod]
    public async Task FallbackCoinGeckoAsync_StaleBinanceUsPrice_IsDowngraded()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("binance.us", StringComparison.Ordinal)
                ? Ok("""{"symbol":"BTCUSDT","lastPrice":"65000.0","priceChangePercent":"2.5"}""")
                : Ok("""{"bitcoin":{"usd":60000.0,"usd_24h_change":1.5}}""");
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        using var feed = new PriceFeedManager(new HttpClient(stub), feedFactory: _ => new FakeFeed()) { Clock = clock };
        feed.Subscribe("bitcoin", AssetKind.Crypto);

        await feed.PollCryptoAsync("BTC");
        clock.Advance(TimeSpan.FromSeconds(61));
        await feed.FallbackCoinGeckoAsync();

        var info = feed.GetPrice("BTC", AssetKind.Crypto);
        Assert.IsNotNull(info);
        Assert.AreEqual(60000m, info.Price, "a stale BinanceUS price may be replaced by the fallback");
        Assert.AreEqual("CoinGecko", info.Source);
    }
}
