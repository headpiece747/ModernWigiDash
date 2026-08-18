using System.Net;
using System.Net.Http;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The REST quote legs: one fetch → parse hop per symbol through the
/// injectable HttpClient seam (the URL shape and the wire parse live in the
/// leg; the price-map store policy stays with the manager), the named
/// source legs' URL shapes and label pins, and the CoinGecko leg's id
/// resolution against the <see cref="SymbolCatalog"/> crypto table.
/// </summary>
[TestClass]
public class PriceRestLegTests
{
    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [TestMethod]
    public async Task FetchAsync_KnownSymbol_FetchesAndReturnsSample()
    {
        var stub = new StubHttpHandler(_ => Ok("{\"price\":1.5,\"change\":0.5}"));
        var leg = new PriceRestLeg(new HttpClient(stub), "Test", "$",
            k => $"https://api.test/v1/quote?symbol={k}",
            (json, _) => json.Contains("1.5", StringComparison.Ordinal) ? new QuoteSample(1.5m, 0.5m) : null);

        var sample = await leg.FetchAsync("ABC", CancellationToken.None);

        Assert.IsTrue(sample.HasValue, "the leg returns the parsed sample");
        Assert.AreEqual(1.5m, sample.Value.Price);
        StringAssert.Contains(stub.RequestUrls[0], "symbol=ABC", "the URL is the leg's to build");
    }

    [TestMethod]
    public async Task FetchAsync_ValidateFails_MakesNoRequest()
    {
        var stub = new StubHttpHandler("{}");
        var leg = new PriceRestLeg(new HttpClient(stub), "Test", "$",
            k => $"https://api.test/{k}",
            (_, _) => new QuoteSample(1m, null),
            _ => false);

        var sample = await leg.FetchAsync("ABC", CancellationToken.None);

        Assert.IsNull(sample, "a symbol that fails the guard is skipped before any request");
        Assert.AreEqual(0, stub.Calls);
    }

    [TestMethod]
    public async Task FetchAsync_UnparseableResponse_ReturnsNullSample()
    {
        var stub = new StubHttpHandler("{}");
        var leg = new PriceRestLeg(new HttpClient(stub), "Test", "$",
            k => $"https://api.test/{k}",
            (json, _) => PriceFeedMessages.TryParseCoinGeckoSimplePrice(json, "testid", out var price, out var change)
                ? new QuoteSample(price, change) : null);

        var sample = await leg.FetchAsync("ABC", CancellationToken.None);

        Assert.IsNull(sample, "a response the parse rejects stores nothing");
        Assert.AreEqual(1, stub.Calls);
    }

    [TestMethod]
    public async Task CoinGeckoFetchAsync_UnknownCoin_MakesNoRequest()
    {
        var stub = new StubHttpHandler("{}");
        var leg = new CoinGeckoRestLeg(new HttpClient(stub));

        var sample = await leg.FetchAsync("ZZZNOPE", CancellationToken.None);

        Assert.IsNull(sample, "a coin outside the catalog resolves no CoinGecko id — no request");
        Assert.AreEqual(0, stub.Calls);
    }

    [TestMethod]
    public async Task CoinGeckoFetchAsync_KnownCoin_ResolvesIdFromSingleTable()
    {
        var stub = new StubHttpHandler(_ => Ok("{\"bitcoin\":{\"usd\":65000,\"usd_24h_change\":2.5}}"));
        var leg = new CoinGeckoRestLeg(new HttpClient(stub));

        var sample = await leg.FetchAsync("BTC", CancellationToken.None);

        Assert.IsTrue(sample.HasValue, "a catalog-known coin resolves and parses");
        Assert.AreEqual(65000m, sample.Value.Price);
        Assert.AreEqual(2.5m, sample.Value.ChangePercent);
        StringAssert.Contains(stub.RequestUrls[0], "ids=bitcoin", "the id comes from the catalog table");
    }

    [TestMethod]
    public async Task CoinGeckoFetchBatch_MixedCoins_FetchesDistinctIdsOnly()
    {
        var stub = new StubHttpHandler(_ => Ok("{\"bitcoin\":{\"usd\":65000,\"usd_24h_change\":1},\"ethereum\":{\"usd\":3000,\"usd_24h_change\":2}}"));
        var leg = new CoinGeckoRestLeg(new HttpClient(stub));

        var samples = await leg.FetchBatchAsync(new[] { "BTC", "ETH", "ZZZNOPE" }, CancellationToken.None);

        Assert.IsNotNull(samples);
        StringAssert.Contains(stub.RequestUrls[0], "ids=bitcoin,ethereum", "unknown coins drop out before the request");
        Assert.AreEqual(65000m, samples["BTC"].Price);
        Assert.AreEqual(3000m, samples["ETH"].Price);
        Assert.IsFalse(samples.ContainsKey("ZZZNOPE"), "a coin with no catalog id appears in no sample");
    }

    [TestMethod]
    public async Task CoinGeckoFetchBatch_NoKnownCoins_ReturnsNullWithoutRequest()
    {
        var stub = new StubHttpHandler("{}");
        var leg = new CoinGeckoRestLeg(new HttpClient(stub));

        var samples = await leg.FetchBatchAsync(new[] { "ZZZNOPE" }, CancellationToken.None);

        Assert.IsNull(samples);
        Assert.AreEqual(0, stub.Calls);
    }

    // ── named source legs: the URL shape and the label, per source ─────

    [TestMethod]
    public async Task BinanceUsLeg_KnownKey_TickerUrlAndSample()
    {
        var stub = new StubHttpHandler(_ => Ok("""{"symbol":"BTCUSDT","lastPrice":"65000.0","priceChangePercent":"2.5"}"""));
        var leg = BinanceUsRestLeg.Create(new HttpClient(stub));

        var sample = await leg.FetchAsync("BTC", CancellationToken.None);

        Assert.IsTrue(sample.HasValue, "a BinanceUS 24hr ticker parses to a sample");
        Assert.AreEqual(65000m, sample.Value.Price);
        Assert.AreEqual(2.5m, sample.Value.ChangePercent);
        StringAssert.Contains(stub.RequestUrls[0], "https://api.binance.us/api/v3/ticker/24hr?symbol=BTCUSDT",
            "the URL is the leg's to build");
        Assert.AreEqual("BinanceUS", leg.SourceLabel, "the source label is the freshness guard's discriminator");
    }

    [TestMethod]
    public async Task FinnhubLeg_KnownSymbol_KeyRidesUrlAndQuoteParses()
    {
        var stub = new StubHttpHandler(_ => Ok("""{"c":150.5,"dp":1.4}"""));
        var leg = FinnhubRestLeg.Create(new HttpClient(stub), "test-key");

        var sample = await leg.FetchAsync("AAPL", CancellationToken.None);

        Assert.IsTrue(sample.HasValue, "a Finnhub quote parses to a sample");
        Assert.AreEqual(150.5m, sample.Value.Price);
        Assert.AreEqual(1.4m, sample.Value.ChangePercent);
        StringAssert.Contains(stub.RequestUrls[0], "https://finnhub.io/api/v1/quote?symbol=AAPL&token=test-key",
            "the API key rides the URL");
    }

    [TestMethod]
    public async Task FinnhubLeg_InvalidSymbol_MakesNoRequest()
    {
        var stub = new StubHttpHandler("{}");
        var leg = FinnhubRestLeg.Create(new HttpClient(stub), "test-key");

        var sample = await leg.FetchAsync("AAPL&x=1", CancellationToken.None);

        Assert.IsNull(sample, "a symbol that fails the guard is skipped before any request");
        Assert.AreEqual(0, stub.Calls);
    }

    [TestMethod]
    public async Task FrankfurterLeg_LiveClock_DateWindowUrlAndSeriesParses()
    {
        var stub = new StubHttpHandler(_ => Ok("""{"rates":{"2026-07-31":{"EUR":1.00},"2026-08-10":{"EUR":1.005}}}"""));
        var leg = FrankfurterRestLeg.Create(
            new HttpClient(stub),
            () => new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));

        var sample = await leg.FetchAsync("USDEUR", CancellationToken.None);

        Assert.IsTrue(sample.HasValue, "a Frankfurter series parses to a sample");
        Assert.AreEqual(1.005m, sample.Value.Price, "the last date is the current rate");
        Assert.AreEqual(0.5m, sample.Value.ChangePercent, "day-over-day from the last two dates");
        Assert.AreEqual("https://api.frankfurter.app/2026-07-31..2026-08-10?from=USD&to=EUR", stub.RequestUrls[0],
            "the date window is the clock's today-10d through today, read at fetch time");
        Assert.AreEqual("", leg.CurrencySymbol, "cross rates carry no currency");
    }

    [TestMethod]
    public async Task YahooChartLeg_KnownSymbol_ChartUrlAndMetaParses()
    {
        var stub = new StubHttpHandler(_ => Ok("""{"chart":{"result":[{"meta":{"regularMarketPrice":165.0,"chartPreviousClose":150.0}}]}}"""));
        var leg = YahooChartRestLeg.Create(new HttpClient(stub));

        var sample = await leg.FetchAsync("AAPL", CancellationToken.None);

        Assert.IsTrue(sample.HasValue, "a Yahoo chart meta block parses to a sample");
        Assert.AreEqual(165m, sample.Value.Price);
        Assert.AreEqual(10m, sample.Value.ChangePercent, "derived from the regular price and the previous close");
        StringAssert.Contains(stub.RequestUrls[0], "https://query1.finance.yahoo.com/v8/finance/chart/AAPL?interval=1d&range=1d",
            "the URL is the leg's to build");
    }
}
