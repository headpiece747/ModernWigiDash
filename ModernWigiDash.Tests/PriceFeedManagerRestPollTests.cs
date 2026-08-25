using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

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
    public void ShouldKeepExisting_FreshOtherSource_True()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var existing = new PriceInfo { Price = 100m, Source = BinanceUsRestLeg.SourceLabel, Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-30) };

        Assert.IsTrue(PriceFeedManager.ShouldKeepExisting(existing, "CoinGecko", clock.GetUtcNow().UtcDateTime),
            "a fresh BinanceUS price must not be downgraded by the CoinGecko fallback");
    }

    [TestMethod]
    public void ShouldKeepExisting_StaleOtherSource_False()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var existing = new PriceInfo { Price = 100m, Source = BinanceUsRestLeg.SourceLabel, Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-61) };

        Assert.IsFalse(PriceFeedManager.ShouldKeepExisting(existing, "CoinGecko", clock.GetUtcNow().UtcDateTime),
            "a stale BinanceUS price may be replaced by the fallback");
    }

    [TestMethod]
    public void ShouldKeepExisting_SameSourceRefresh_False()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var existing = new PriceInfo { Price = 100m, Source = "CoinGecko", Timestamp = clock.GetUtcNow().UtcDateTime };

        Assert.IsFalse(PriceFeedManager.ShouldKeepExisting(existing, "CoinGecko", clock.GetUtcNow().UtcDateTime),
            "a same-source refresh must replace the previous fallback sample");
    }

    [TestMethod]
    public void ShouldKeepExisting_FreshWebSocketBinance_True()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var existing = new PriceInfo { Price = 100m, Source = "Binance", Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-30) };

        Assert.IsTrue(PriceFeedManager.ShouldKeepExisting(existing, "CoinGecko", clock.GetUtcNow().UtcDateTime),
            "the live Binance WebSocket price is protected from the CoinGecko fallback too");
    }

    [TestMethod]
    public void ShouldKeepExisting_FreshFinnhubAgainstYahoo_True()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var existing = new PriceInfo { Price = 150.5m, Source = "Finnhub", Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-30) };

        Assert.IsTrue(PriceFeedManager.ShouldKeepExisting(existing, "Yahoo", clock.GetUtcNow().UtcDateTime),
            "a fresh Finnhub stock price must not be downgraded by the Yahoo one-shot seed");
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
        Assert.AreEqual(BinanceUsRestLeg.SourceLabel, info.Source);
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

    // ── one-shot seed: the manager-owned operation behind the ticker ──

    [TestMethod]
    public async Task SeedFallbackAsync_Crypto_FreshLivePrice_IsNotDowngraded()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("binance.us", StringComparison.Ordinal)
                ? Ok("""{"symbol":"BTCUSDT","lastPrice":"65000.0","priceChangePercent":"2.5"}""")
                : Ok("""{"bitcoin":{"usd":60000.0,"usd_24h_change":1.5}}""");
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        using var feed = new PriceFeedManager(new HttpClient(stub)) { Clock = clock };

        await feed.PollCryptoAsync("BTC"); // the live cycle stores a fresh BinanceUS record
        await feed.SeedFallbackAsync("BTC", AssetKind.Crypto);

        var info = feed.GetPrice("BTC", AssetKind.Crypto);
        Assert.IsNotNull(info);
        Assert.AreEqual(65000m, info.Price, "a fresh BinanceUS price must survive the one-shot seed");
        Assert.AreEqual(BinanceUsRestLeg.SourceLabel, info.Source);

        clock.Advance(TimeSpan.FromSeconds(90)); // the live record is now stale
        await feed.SeedFallbackAsync("BTC", AssetKind.Crypto);

        info = feed.GetPrice("BTC", AssetKind.Crypto);
        Assert.IsNotNull(info);
        Assert.AreEqual(60000m, info.Price, "a stale live price may be replaced by the seed");
        Assert.AreEqual("CoinGecko", info.Source);
    }

    [TestMethod]
    public async Task SeedFallbackAsync_Stock_FreshFinnhubPrice_IsNotDowngraded()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("finnhub.io", StringComparison.Ordinal)
                ? Ok("""{"c":150.5,"d":2.1,"dp":1.4,"h":152,"l":148,"o":148.5,"pc":148.5}""")
                : Ok("""{"chart":{"result":[{"meta":{"regularMarketPrice":140.0,"chartPreviousClose":138.0}}]}}""");
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key") { Clock = clock };

        await feed.PollStockAsync("AAPL");
        await feed.SeedFallbackAsync("AAPL", AssetKind.Stock);

        var info = feed.GetPrice("AAPL", AssetKind.Stock);
        Assert.IsNotNull(info);
        Assert.AreEqual(150.5m, info.Price, "a fresh Finnhub price must survive the Yahoo one-shot seed");
        Assert.AreEqual("Finnhub", info.Source);

        clock.Advance(TimeSpan.FromSeconds(90));
        await feed.SeedFallbackAsync("AAPL", AssetKind.Stock);

        info = feed.GetPrice("AAPL", AssetKind.Stock);
        Assert.IsNotNull(info);
        Assert.AreEqual(140.0m, info.Price, "a stale Finnhub price may be replaced by the seed");
        Assert.AreEqual("Yahoo", info.Source);
    }

    [TestMethod]
    public async Task SeedFallbackAsync_Fx_MakesNoRequest()
    {
        var stub = new StubHttpHandler("{}");
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key");

        await feed.SeedFallbackAsync("EUR/USD", AssetKind.Fx);

        Assert.AreEqual(0, stub.Calls, "FX is served by the Frankfurter cycle — the one-shot seed must make no request");
    }

    [TestMethod]
    public async Task Subscribe_SecondFxPairAfterLoopStart_IsPolledOnTheNextCycle()
    {
        // Regression: the REST cycle used to receive the subscription set's
        // keys at first claim. A ConcurrentDictionary.Keys is a snapshot, and
        // the loop task never restarts, so a pair subscribed later was never
        // polled (its widget sat on a blank price). The loop now reads the
        // live membership each cycle; a late pair is polled on the next one.
        // The delay parks at a gate the test releases, so the cadence is
        // driven one cycle at a time.
        var stub = new StubHttpHandler(_ => Ok("""{"rates":{"2025-08-01":{"USD":1.08},"2025-08-02":{"USD":1.09}}}"""));
        var gates = new System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource>();
        Func<TimeSpan, CancellationToken, Task> delay = (_, ct) =>
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gates.Enqueue(tcs);
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        };
        using var feed = new PriceFeedManager(new HttpClient(stub), delay: delay);

        // The loop parks at its next delay gate right after each cycle, so
        // dequeuing the parked gate and completing it drives exactly one cycle.
        TaskCompletionSource? gate = null;

        feed.Subscribe("EUR/USD", AssetKind.Fx); // first claim starts the FX REST loop
        await TestWait.WaitUntilAsync(() => gates.TryDequeue(out gate), TimeSpan.FromSeconds(5));
        gate!.TrySetResult();
        await TestWait.WaitUntilAsync(() => feed.GetPrice("EUR/USD", AssetKind.Fx) is not null, TimeSpan.FromSeconds(5));

        feed.Subscribe("GBP/USD", AssetKind.Fx); // a second widget joins while the loop is parked
        TaskCompletionSource? secondGate = null;
        await TestWait.WaitUntilAsync(() => gates.TryDequeue(out secondGate), TimeSpan.FromSeconds(5));
        secondGate!.TrySetResult();
        await TestWait.WaitUntilAsync(() => feed.GetPrice("GBP/USD", AssetKind.Fx) is not null, TimeSpan.FromSeconds(5));

        var gbpusd = feed.GetPrice("GBP/USD", AssetKind.Fx)!;
        Assert.AreEqual(1.09m, gbpusd.Price, "the late pair must be polled and its price stored");
        Assert.AreEqual("Frankfurter", gbpusd.Source);
        Assert.IsTrue(stub.RequestUrls.Any(u => u.Contains("from=GBP&to=USD", StringComparison.Ordinal)),
            "the Frankfurter cycle must request the late pair's series");
    }
}
