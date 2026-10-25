using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

/// <summary>
/// The bounded 24-slot LRU poster cache's rules: the capacity eviction
/// (25 inserts retire the oldest), the render-thread retirement drain
/// (an evicted texture is disposed after the drain, a live one is not),
/// the LRU recency refresh, and the scaled-decode contract (the decode
/// lands in the target-dimension Rgb565 bitmap, never the raw resolution,
/// and never upscales).
/// </summary>
[TestClass]
public sealed class SteamPosterCacheTests
{
    private static SKBitmap MakeTestBitmap()
        => new(4, 4, SKColorType.Rgb565, SKAlphaType.Opaque);

    /// <summary>A real JPEG of the requested size (the decode tests need a
    /// genuine image, not a fabricated one).</summary>
    private static byte[] MakeJpeg(int width, int height)
    {
        using var source = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(source);
        canvas.Clear(new SKColor(30, 90, 160));
        using SKImage image = SKImage.FromBitmap(source);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90)?.ToArray() ?? [];
    }

    [TestMethod]
    public void Store_25Items_EvictsTheOldest()
    {
        using var cache = new SteamPosterCache();

        for (uint appId = 1; appId <= 25; appId++)
            cache.InsertForTest(appId, MakeTestBitmap());

        Assert.AreEqual(SteamPosterCache.Capacity, cache.Count);
        Assert.IsFalse(cache.TryGet(1u, out _)); // the oldest is gone.
        Assert.IsTrue(cache.TryGet(2u, out _)); // the rest survive.
        Assert.IsTrue(cache.TryGet(25u, out _));
    }

    [TestMethod]
    public void EvictedTexture_IsDisposedAfterTheRenderThreadDrain()
    {
        using var cache = new SteamPosterCache();
        for (uint appId = 1; appId <= SteamPosterCache.Capacity + 1; appId++)
            cache.InsertForTest(appId, MakeTestBitmap());

        // The eviction retired exactly the oldest texture into the
        // retirement set: nothing is disposed until the render thread
        // drains (the UAF rule). A live texture is never retired.
        Assert.AreEqual(1, cache.RetirementSet.PendingCount, "only the evicted texture is pending disposal");
        Assert.IsFalse(cache.TryGet(1u, out _), "the evicted texture is no longer cached");
        Assert.IsTrue(cache.TryGet(2u, out _), "a live texture survives the eviction");

        cache.DisposeRetired();

        Assert.AreEqual(0, cache.RetirementSet.PendingCount, "the drain disposes the evicted texture");
        Assert.IsTrue(cache.TryGet(2u, out _), "a live texture is never disposed by a drain");
    }

    [TestMethod]
    public void LRU_RecencyRefresh_KeepsTheTouchedEntry()
    {
        using var cache = new SteamPosterCache();
        var bitmaps = new Dictionary<uint, SKBitmap>();
        for (uint appId = 1; appId <= SteamPosterCache.Capacity; appId++)
        {
            bitmaps[appId] = MakeTestBitmap();
            cache.InsertForTest(appId, bitmaps[appId]);
        }

        // Touch the oldest: it becomes the most recently used.
        Assert.IsTrue(cache.TryGet(1u, out _));
        cache.InsertForTest(100u, MakeTestBitmap());

        Assert.IsTrue(cache.TryGet(1u, out _), "the touched entry survives the eviction");
        Assert.IsFalse(cache.TryGet(2u, out _), "the next-oldest is evicted instead");
    }

    [TestMethod]
    public void DecodeScaled_GridBounds_ProducesRgb565AtTheTargetSize()
    {
        byte[] jpeg = MakeJpeg(600, 900); // the librarycache poster shape.

        using SKBitmap? decoded = SteamPosterCache.DecodeScaled(jpeg, 110, 165);

        Assert.IsNotNull(decoded);
        Assert.AreEqual(110, decoded.Width);
        Assert.AreEqual(165, decoded.Height);
        Assert.AreEqual(SKColorType.Rgb565, decoded.ColorType);
    }

    [TestMethod]
    public void DecodeScaled_ShowcaseBounds_ProducesTheShowcaseSize()
    {
        byte[] jpeg = MakeJpeg(600, 900);

        using SKBitmap? decoded = SteamPosterCache.DecodeScaled(jpeg, 180, 270);

        Assert.IsNotNull(decoded);
        Assert.AreEqual(180, decoded.Width);
        Assert.AreEqual(270, decoded.Height);
        Assert.AreEqual(SKColorType.Rgb565, decoded.ColorType);
    }

    [TestMethod]
    public void DecodeScaled_SmallerSource_NeverUpscales()
    {
        byte[] jpeg = MakeJpeg(60, 90);

        using SKBitmap? decoded = SteamPosterCache.DecodeScaled(jpeg, 110, 165);

        Assert.IsNotNull(decoded);
        Assert.AreEqual(60, decoded.Width);
        Assert.AreEqual(90, decoded.Height);
    }

    [TestMethod]
    public void DecodeScaled_NotAnImage_ReturnsNull()
    {
        Assert.IsNull(SteamPosterCache.DecodeScaled([1, 2, 3, 4], 110, 165));
    }

    [TestMethod]
    public void Store_NonImageBytes_StoresNothing()
    {
        using var cache = new SteamPosterCache();

        cache.Store(730u, [1, 2, 3, 4], 110, 165);

        Assert.AreEqual(0, cache.Count);
        Assert.IsFalse(cache.TryGet(730u, out _));
    }

    [TestMethod]
    public void Dispose_Teardown_DisposesEveryCachedBitmap()
    {
        var cache = new SteamPosterCache();
        cache.InsertForTest(730u, MakeTestBitmap());
        cache.InsertForTest(731u, MakeTestBitmap());

        cache.Dispose();

        Assert.AreEqual(0, cache.Count, "teardown empties the cache");
        Assert.AreEqual(0, cache.RetirementSet.PendingCount, "teardown disposes the pending retirements");
    }
}
