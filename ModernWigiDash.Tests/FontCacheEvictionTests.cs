using System.Collections.Concurrent;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Tests;

/// <summary>
/// The font cache overflow-bound rule (the shared clear-on-overflow reset
/// that bounds FontHelper's memoization caches) — the boundary is exclusive
/// (strictly greater than the declared limit) and eviction is a whole-cache
/// reset.
/// </summary>
[TestClass]
public class FontCacheEvictionTests
{
    private static ConcurrentDictionary<int, int> CacheOf(int count)
    {
        var cache = new ConcurrentDictionary<int, int>();
        for (int i = 1; i <= count; i++) cache[i] = i;
        return cache;
    }

    [TestMethod]
    public void EvictIfFull_BelowLimit_KeepsEntries()
    {
        var cache = CacheOf(1);

        FontCacheEviction.EvictIfFull(cache, limit: 2);

        Assert.AreEqual(1, cache.Count, "below the limit: nothing evicts");
    }

    [TestMethod]
    public void EvictIfFull_AtLimit_KeepsEntries()
    {
        var cache = CacheOf(2);

        FontCacheEviction.EvictIfFull(cache, limit: 2);

        Assert.AreEqual(2, cache.Count, "at exactly the limit: still nothing — the rule is strictly greater-than");
    }

    [TestMethod]
    public void EvictIfFull_AboveLimit_ClearsEntireCache()
    {
        var cache = CacheOf(3);

        FontCacheEviction.EvictIfFull(cache, limit: 2);

        Assert.AreEqual(0, cache.Count, "past the limit: the whole cache resets (entries refill on demand)");
    }
}
