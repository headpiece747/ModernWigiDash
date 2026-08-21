using System.Net.Http;

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

    [TestMethod]
    public void CryptoStockTickerWidget_CustomColors_RendersWithoutExceptions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        var canvas = surface.Canvas;
        var widget = new CryptoStockTickerWidget
        {
            TextColorHex = "#C6E0FF",
            PositiveColorHex = "#22C55E",
            NegativeColorHex = "#EF4444"
        };
        widget.Render(canvas, new SKRect(0, 0, 200, 150));

        Assert.IsNotNull(surface);
    }

    // ── TickerStalenessPresentation: the stale-price display rules ─────────

    [TestMethod]
    public void TickerStaleness_IsStale_MissingRecord_True()
    {
        Assert.IsTrue(TickerStalenessPresentation.IsStale(null), "no price must never look live");
    }

    [TestMethod]
    public void TickerStaleness_IsStale_FreshRecord_False()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var info = new PriceInfo { Price = 100m, Timestamp = clock.GetUtcNow().UtcDateTime, Clock = clock };

        Assert.IsFalse(TickerStalenessPresentation.IsStale(info));
    }

    [TestMethod]
    public void TickerStaleness_IsStale_StaleRecord_True()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var info = new PriceInfo { Price = 100m, Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-61), Clock = clock };

        Assert.IsTrue(TickerStalenessPresentation.IsStale(info));
    }

    [TestMethod]
    public void TickerStaleness_BadgeText_Stale_GetsFreshnessDot()
    {
        Assert.AreEqual("• +1.5%", TickerStalenessPresentation.BadgeText("+1.5%", isStale: true));
        Assert.AreEqual("+1.5%", TickerStalenessPresentation.BadgeText("+1.5%", isStale: false));
    }

    [TestMethod]
    public void TickerStaleness_StaleBadgeAlpha_NeutralGray()
    {
        Assert.AreEqual(120, TickerStalenessPresentation.StaleBadgeAlpha);
    }

    [TestMethod]
    public async Task Render_NoPrice_ReseedsAtMostOncePer15Seconds()
    {
        // The recovery policy: when the feed has no price yet, Render re-seeds
        // the one-shot fallback at most once per 15s (the FeedSubscription
        // seed covers the immediate case; this covers the failed-seed retry).
        var stub = new StubHttpHandler(_ => StubHttpHandler.NotFound());
        using var feed = new PriceFeedManager(new HttpClient(stub), "test-key", feedFactory: _ => new FakeFeed(), reconnectDelay: TimeSpan.FromMilliseconds(10));
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var widget = new CryptoStockTickerWidget { Symbol = "BTC", AssetType = "Crypto", Feed = feed, Clock = clock };
        using var surface = SKSurface.Create(new SKImageInfo(203, 148));
        var rect = new SKRect(0, 0, 203, 148);

        widget.Render(surface.Canvas, rect);
        await TestWait.WaitUntilAsync(() => stub.Calls >= 1, TimeSpan.FromSeconds(5));
        int afterFirst = stub.Calls;

        widget.Render(surface.Canvas, rect);
        await Task.Delay(50);
        Assert.AreEqual(afterFirst, stub.Calls, "within the 15s window no re-seed may fire");

        clock.Advance(TimeSpan.FromSeconds(16));
        widget.Render(surface.Canvas, rect);
        await TestWait.WaitUntilAsync(() => stub.Calls > afterFirst, TimeSpan.FromSeconds(5));
    }
}
