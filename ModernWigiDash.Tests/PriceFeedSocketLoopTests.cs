using System.IO;
using System.Net;
using System.Net.Http;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// Tests the price-feed WebSocket seam: the Binance/Finnhub loops are driven by
/// an in-memory feed (no live socket), the reconnect policy is exercised, and
/// the single crypto symbol table cannot silently lose a CoinGecko fallback.
/// </summary>
[TestClass]
public class PriceFeedSocketLoopTests
{
    private sealed class NeverHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class FakeFeed : IWebSocketFeed
    {
        private readonly Queue<string> _incoming = new();
        public List<string> Sent { get; } = [];
        public int ConnectCount { get; private set; }
        public bool IsOpen { get; set; } = true;
        public Exception? ConnectError { get; set; }

        public void QueueMessage(string message) => _incoming.Enqueue(message);

        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            ConnectCount++;
            return ConnectError is null ? Task.CompletedTask : Task.FromException(ConnectError);
        }

        public Task SendTextAsync(string payload, CancellationToken ct)
        {
            Sent.Add(payload);
            return Task.CompletedTask;
        }

        public Task<string?> ReceiveTextAsync(CancellationToken ct)
            => Task.FromResult(_incoming.Count > 0 ? _incoming.Dequeue() : null);

        public void Abort() { }
        public void Dispose() { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(20);
        }
        Assert.IsTrue(condition(), "Condition was not met within timeout");
    }

    [TestMethod]
    public async Task BinanceLoop_AppliesTickerFromFeed_UpdatesPrice()
    {
        var feed = new FakeFeed();
        using var manager = new PriceFeedManager(
            new HttpClient(new NeverHttpHandler()),
            feedFactory: _ => feed,
            reconnectDelay: TimeSpan.FromMilliseconds(20));
        feed.QueueMessage("""{"e":"24hrTicker","s":"BTCUSDT","c":"65432.10","P":"1.23"}""");

        manager.Subscribe("BTC", AssetKind.Crypto);

        await WaitUntilAsync(() => manager.GetPrice("BTC", AssetKind.Crypto) is not null);

        var price = manager.GetPrice("BTC", AssetKind.Crypto)!;
        Assert.AreEqual(65432.10m, price.Price);
        Assert.AreEqual(1.23m, price.ChangePercent);
        Assert.AreEqual("Binance", price.Source);
        Assert.IsTrue(feed.Sent.Count > 0, "The loop must send the connect-time subscription payload");
    }

    [TestMethod]
    public async Task BinanceLoop_AfterConnectFault_Reconnects()
    {
        var feed = new FakeFeed();
        using var manager = new PriceFeedManager(
            new HttpClient(new NeverHttpHandler()),
            feedFactory: _ => feed,
            reconnectDelay: TimeSpan.FromMilliseconds(20));

        manager.Subscribe("BTC", AssetKind.Crypto);

        // First connect attempt fails; the loop must try again after the delay.
        await WaitUntilAsync(() => feed.ConnectCount >= 1);
        feed.ConnectError = new IOException("socket fault");
        await WaitUntilAsync(() => feed.ConnectCount >= 2);
        Assert.IsTrue(feed.ConnectCount >= 2, "A failed connect must trigger a reconnect attempt");
    }

    [TestMethod]
    public void CryptoAliasTable_EveryBaseCoinResolvesAsItsOwnKey_WithCoinGeckoId()
    {
        foreach (string baseCoin in PriceFeedManager.CryptoAliases.Values.Select(a => a.Symbol).Distinct())
        {
            Assert.IsTrue(
                PriceFeedManager.CryptoAliases.TryGetValue(baseCoin, out var alias),
                $"{baseCoin} must resolve as its own alias key so the CoinGecko fallback can find it");
            Assert.AreEqual(baseCoin, alias.Symbol);
            Assert.IsFalse(string.IsNullOrEmpty(alias.CoinGeckoId), $"{baseCoin} must have a CoinGecko id");
        }
    }

    [TestMethod]
    public async Task FetchFallbackAsync_CryptoWithKnownAlias_UsesCoinGeckoIdFromSingleTable()
    {
        var stub = new HttpMessageHandlerStub("""{"arbitrum":{"usd":1.05,"usd_24h_change":2.5}}""");
        using var manager = new PriceFeedManager(new HttpClient(stub), "test-key");

        // "arbitrum" is not the canonical symbol — the fallback must still
        // resolve its CoinGecko id through the single table (old two-table
        // layout silently dropped coins whose canonical symbol was not a key).
        await manager.FetchFallbackAsync("ARB", AssetKind.Crypto);

        Assert.AreEqual(1, stub.Calls);
        Assert.IsTrue(stub.RequestUrls[0].Contains("arbitrum", StringComparison.Ordinal), "Fallback URL must use the CoinGecko id");
        var price = manager.GetPrice("ARB", AssetKind.Crypto)!;
        Assert.AreEqual(1.05m, price.Price);
        Assert.AreEqual("CoinGecko", price.Source);
    }

    private sealed class HttpMessageHandlerStub(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> RequestUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestUrls.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
