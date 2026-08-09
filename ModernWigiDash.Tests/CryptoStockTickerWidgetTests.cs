using System.Net;
using System.Net.Http;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// The ticker's feed subscription lifecycle — the NowPlaying shape: subscribe
/// at init, re-subscribe on property change, Render stays a pure draw.
/// </summary>
[TestClass]
public class CryptoStockTickerWidgetTests
{

    private static PriceFeedManager CreateOfflineFeed() => new(
        new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())),
        "test-key",
        feedFactory: _ => new FakeFeed(),
        reconnectDelay: TimeSpan.FromMilliseconds(10));

    [TestMethod]
    public async Task InitializeAsync_SubscribesTheSymbol()
    {
        var feed = CreateOfflineFeed();
        var widget = new CryptoStockTickerWidget { Symbol = "BTC", AssetType = "Crypto", Feed = feed };

        await widget.InitializeAsync(new TestContext());

        Assert.IsTrue(feed._subscribedCrypto.ContainsKey("BTC"), "Init must subscribe the symbol");
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async Task OnPropertyChanged_ResubscribesNewSymbol_UnsubscribesOld()
    {
        var feed = CreateOfflineFeed();
        var widget = new CryptoStockTickerWidget { Symbol = "BTC", AssetType = "Crypto", Feed = feed };
        await widget.InitializeAsync(new TestContext());

        widget.Symbol = "ETH";
        widget.OnPropertyChanged(nameof(CryptoStockTickerWidget.Symbol), "ETH");

        Assert.IsTrue(feed._subscribedCrypto.ContainsKey("ETH"), "A symbol change must subscribe the new symbol");
        Assert.IsFalse(feed._subscribedCrypto.ContainsKey("BTC"), "A symbol change must unsubscribe the old symbol");
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async Task Render_DoesNotSubscribe()
    {
        var feed = CreateOfflineFeed();
        var widget = new CryptoStockTickerWidget { Symbol = "BTC", AssetType = "Crypto", Feed = feed };
        await widget.InitializeAsync(new TestContext());

        // Render a symbol that was never subscribed through the lifecycle —
        // a pure draw must not mutate feed state.
        widget.Symbol = "SOL";
        using var surface = SKSurface.Create(new SKImageInfo(203, 148));
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        Assert.IsFalse(feed._subscribedCrypto.ContainsKey("SOL"), "Render must not subscribe");
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async Task Dispose_Unsubscribes()
    {
        var feed = CreateOfflineFeed();
        var widget = new CryptoStockTickerWidget { Symbol = "BTC", AssetType = "Crypto", Feed = feed };
        await widget.InitializeAsync(new TestContext());

        await widget.DisposeAsync();

        Assert.IsFalse(feed._subscribedCrypto.ContainsKey("BTC"), "Dispose must stop polling the symbol");
    }

    [TestMethod]
    public void CryptoStockTickerWidget_FxPair_RendersWithoutExceptions()
    {
        var widget = new CryptoStockTickerWidget { Symbol = "EUR/USD" };
        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        var canvas = surface.Canvas;
        widget.Render(canvas, new SKRect(0, 0, 200, 150));
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void CryptoStockTickerWidget_EmptySymbol_RendersPlaceholderWithoutExceptions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        var canvas = surface.Canvas;
        var widget = new CryptoStockTickerWidget { Symbol = "   " };
        widget.Render(canvas, new SKRect(0, 0, 200, 150));

        Assert.IsNotNull(surface);
    }
}
