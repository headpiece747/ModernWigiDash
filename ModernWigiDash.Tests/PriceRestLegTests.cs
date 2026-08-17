using System.Net;
using System.Net.Http;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The REST quote legs: one fetch → parse hop per symbol through the
/// injectable HttpClient seam (the URL shape and the wire parse live in the
/// leg; the price-map store policy stays with the manager), plus the
/// CoinGecko leg's id resolution against the <see cref="SymbolCatalog"/>
/// crypto table.
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
}
