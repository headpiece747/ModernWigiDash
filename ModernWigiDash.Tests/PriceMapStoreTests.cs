namespace ModernWigiDash.Tests;

/// <summary>
/// The price-map store seam: the one owner of the shared price map and of
/// both merge rules (live overwrite, the fallback downgrade guard) over one
/// clock. Driven directly with a fake clock, no manager, no HTTP. The
/// manager-level behavior (poll/seed/fallback through GetPrice) stays pinned
/// in PriceFeedManagerRestPollTests.
/// </summary>
[TestClass]
public class PriceMapStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static (PriceMapStore store, FakeTimeProvider clock) StoreAt()
    {
        var clock = new FakeTimeProvider(T0);
        return (new PriceMapStore(() => clock.GetUtcNow().UtcDateTime), clock);
    }

    // ── the live rule: always overwrites, change-keep on null ─────────

    [TestMethod]
    public void ApplyLive_FirstWrite_StoresPriceSourceTimestampAndCurrency()
    {
        var (store, clock) = StoreAt();

        store.ApplyLive("BTC", 65000m, 2.5m, "Binance");

        var info = store.TryGet("BTC")!;
        Assert.AreEqual(65000m, info.Price);
        Assert.AreEqual(2.5m, info.ChangePercent);
        Assert.AreEqual("Binance", info.Source);
        Assert.AreEqual("$", info.CurrencySymbol, "the default currency symbol is the dollar");
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime, info.Timestamp, "the stamp is the store's clock at write time");
    }

    [TestMethod]
    public void ApplyLive_NullChange_KeepsTheExistingChange()
    {
        var (store, _) = StoreAt();
        store.ApplyLive("AAPL", 150.5m, 1.4m, "Finnhub");

        // A trade message carries no change figure (the Finnhub WS shape):
        // the known change survives the price update.
        store.ApplyLive("AAPL", 151.0m, null, "Finnhub");

        var info = store.TryGet("AAPL")!;
        Assert.AreEqual(151.0m, info.Price);
        Assert.AreEqual(1.4m, info.ChangePercent, "a null-change sample must not zero the previously known change");
    }

    [TestMethod]
    public void ApplyLive_FirstWriteNullChange_StoresZero()
    {
        var (store, _) = StoreAt();

        store.ApplyLive("AAPL", 150.5m, null, "Finnhub");

        Assert.AreEqual(0m, store.TryGet("AAPL")!.ChangePercent, "with no existing record a null change is zero");
    }

    [TestMethod]
    public void ApplyLive_StaleSameSourceRecord_StillOverwrites()
    {
        var (store, clock) = StoreAt();
        store.ApplyLive("BTC", 65000m, 2.5m, "Binance");
        clock.Advance(TimeSpan.FromSeconds(3600));

        // The live rule has no freshness guard: a live sample always wins,
        // from any source, at any age.
        store.ApplyLive("BTC", 66000m, 3.0m, "Binance");

        var info = store.TryGet("BTC")!;
        Assert.AreEqual(66000m, info.Price);
        Assert.AreEqual(3.0m, info.ChangePercent);
    }

    // ── the fallback rule: the downgrade guard, same change-keep ──────

    [TestMethod]
    public void ApplyFallback_FreshOtherSource_KeepsTheExisting()
    {
        var (store, clock) = StoreAt();
        store.ApplyLive("BTC", 65000m, 2.5m, "BinanceUS");
        clock.Advance(TimeSpan.FromSeconds(30));

        store.ApplyFallback("BTC", 60000m, 1.5m, "CoinGecko");

        var info = store.TryGet("BTC")!;
        Assert.AreEqual(65000m, info.Price, "a fresh live price must not be downgraded by the fallback");
        Assert.AreEqual("BinanceUS", info.Source);
    }

    [TestMethod]
    public void ApplyFallback_StaleOtherSource_Replaces()
    {
        var (store, clock) = StoreAt();
        store.ApplyLive("BTC", 65000m, 2.5m, "BinanceUS");
        clock.Advance(TimeSpan.FromSeconds(61));

        // The guard reads the clock at write time (the live-read seam): a
        // post-construction clock advance is what makes the record stale.
        store.ApplyFallback("BTC", 60000m, 1.5m, "CoinGecko");

        var info = store.TryGet("BTC")!;
        Assert.AreEqual(60000m, info.Price, "a stale live price may be replaced by the fallback");
        Assert.AreEqual("CoinGecko", info.Source);
        Assert.AreEqual(1.5m, info.ChangePercent);
    }

    [TestMethod]
    public void ApplyFallback_SameSourceRefresh_Replaces()
    {
        var (store, _) = StoreAt();
        store.ApplyFallback("BTC", 60000m, 1.5m, "CoinGecko");

        store.ApplyFallback("BTC", 61000m, 1.8m, "CoinGecko");

        var info = store.TryGet("BTC")!;
        Assert.AreEqual(61000m, info.Price, "a same-source fallback refresh must replace the previous sample");
        Assert.AreEqual(1.8m, info.ChangePercent);
    }

    [TestMethod]
    public void ApplyFallback_NullChange_KeepsTheExistingChange()
    {
        var (store, clock) = StoreAt();
        store.ApplyFallback("BTC", 60000m, 1.5m, "CoinGecko");
        clock.Advance(TimeSpan.FromSeconds(61));

        // A re-seed whose response omits the 24h change keeps the previous
        // change instead of zeroing it (the rule the batch tail spelled by
        // hand, now the one change resolution).
        store.ApplyFallback("BTC", 61000m, null, "CoinGecko");

        var info = store.TryGet("BTC")!;
        Assert.AreEqual(61000m, info.Price);
        Assert.AreEqual(1.5m, info.ChangePercent, "a null-change fallback sample must not zero the previously known change");
    }

    // ── the read/release seams ────────────────────────────────────────

    [TestMethod]
    public void TryGet_MissingKey_ReturnsNull()
    {
        var (store, _) = StoreAt();

        Assert.IsNull(store.TryGet("ETH"), "a key that was never stored is absent from the map");
    }

    [TestMethod]
    public void TryRemove_FullyReleasedKey_RemovesTheRecord()
    {
        var (store, _) = StoreAt();
        store.ApplyLive("BTC", 65000m, 2.5m, "Binance");

        Assert.IsTrue(store.TryRemove("BTC"));
        Assert.IsNull(store.TryGet("BTC"), "a fully-released symbol's record leaves the map");
    }

    // ── the downgrade guard, pure (moved from the manager) ────────────

    [TestMethod]
    public void ShouldKeepExisting_FreshOtherSource_True()
    {
        var clock = new FakeTimeProvider(T0);
        var existing = new PriceInfo { Price = 100m, Source = BinanceUsRestLeg.SourceLabel, Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-30) };

        Assert.IsTrue(PriceMapStore.ShouldKeepExisting(existing, "CoinGecko", clock.GetUtcNow().UtcDateTime),
            "a fresh BinanceUS price must not be downgraded by the CoinGecko fallback");
    }

    [TestMethod]
    public void ShouldKeepExisting_StaleOtherSource_False()
    {
        var clock = new FakeTimeProvider(T0);
        var existing = new PriceInfo { Price = 100m, Source = BinanceUsRestLeg.SourceLabel, Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-61) };

        Assert.IsFalse(PriceMapStore.ShouldKeepExisting(existing, "CoinGecko", clock.GetUtcNow().UtcDateTime),
            "a stale BinanceUS price may be replaced by the fallback");
    }

    [TestMethod]
    public void ShouldKeepExisting_SameSourceRefresh_False()
    {
        var clock = new FakeTimeProvider(T0);
        var existing = new PriceInfo { Price = 100m, Source = "CoinGecko", Timestamp = clock.GetUtcNow().UtcDateTime };

        Assert.IsFalse(PriceMapStore.ShouldKeepExisting(existing, "CoinGecko", clock.GetUtcNow().UtcDateTime),
            "a same-source refresh must replace the previous fallback sample");
    }

    [TestMethod]
    public void ShouldKeepExisting_FreshWebSocketBinance_True()
    {
        var clock = new FakeTimeProvider(T0);
        var existing = new PriceInfo { Price = 100m, Source = "Binance", Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-30) };

        Assert.IsTrue(PriceMapStore.ShouldKeepExisting(existing, "CoinGecko", clock.GetUtcNow().UtcDateTime),
            "the live Binance WebSocket price is protected from the CoinGecko fallback too");
    }

    [TestMethod]
    public void ShouldKeepExisting_FreshFinnhubAgainstYahoo_True()
    {
        var clock = new FakeTimeProvider(T0);
        var existing = new PriceInfo { Price = 150.5m, Source = "Finnhub", Timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-30) };

        Assert.IsTrue(PriceMapStore.ShouldKeepExisting(existing, "Yahoo", clock.GetUtcNow().UtcDateTime),
            "a fresh Finnhub stock price must not be downgraded by the Yahoo one-shot seed");
    }
}
