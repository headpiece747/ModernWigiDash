using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Tests;

/// <summary>
/// The <see cref="WrapCache"/> module — the bounded LRU memo of word-wrap
/// results and the greedy wrap rule it owns (private to the cache, the wrap
/// result's only consumer). The word-wrap semantics pin through the cache's
/// GetOrWrap surface: one distinct text, one key, no re-wrap on a hit.
/// </summary>
[TestClass]
public class WrapCacheTests
{
    [TestMethod]
    public void WrapCache_SameTextAndSize_ReturnsSameInstance()
    {
        var cache = new WrapCache();
        var font = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 24f);
        const string text = "one two three four five six seven eight nine ten";

        var first = cache.GetOrWrap(text, font, 24f, 100f);
        var second = cache.GetOrWrap(text, font, 24f, 100f);

        Assert.AreSame(first, second, "an unchanged key must not re-wrap");
        Assert.IsTrue(first.Count > 1, "the long text must actually wrap at the narrow width");
    }

    [TestMethod]
    public void WrapLongText_SplitsIntoMultipleLines()
    {
        var cache = new WrapCache();
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);

        var lines = cache.GetOrWrap("one two three four five six", font, 12f, 50f);

        Assert.IsTrue(lines.Count >= 2, "the text must wrap onto multiple lines");
        Assert.AreEqual("one two three four five six", string.Join(" ", lines));
    }

    [TestMethod]
    public void WrapCache_DifferentWidth_ReWraps()
    {
        var cache = new WrapCache();
        var font = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 24f);
        const string text = "one two three four five six seven eight nine ten";

        var narrow = cache.GetOrWrap(text, font, 24f, 100f);
        var wide = cache.GetOrWrap(text, font, 24f, 2000f);

        Assert.AreNotSame(narrow, wide, "a width change must re-wrap");
        Assert.AreEqual(1, wide.Count, "the wide width fits the whole text on one line");
        Assert.AreEqual(text, wide[0]);
    }

    [TestMethod]
    public void WrapCache_DifferentFontSize_ReWraps()
    {
        var cache = new WrapCache();
        var small = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 24f);
        var large = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 48f);
        const string text = "one two three four five six seven eight nine ten";

        var first = cache.GetOrWrap(text, small, 24f, 150f);
        var second = cache.GetOrWrap(text, large, 48f, 150f);

        Assert.AreNotSame(first, second, "a font-size change must re-wrap");
    }

    [TestMethod]
    public void WrapCache_SplitLines_EachLineWrappedIndependently()
    {
        var cache = new WrapCache();
        var font = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 24f);

        var lines = cache.GetOrWrap("short\none two three four five six seven eight", font, 24f, 100f);

        Assert.AreEqual("short", lines[0], "an explicit newline becomes its own line");
        Assert.IsTrue(lines.Count >= 3);
    }

    [TestMethod]
    public void WrapCache_Instances_AreIsolated()
    {
        var first = new WrapCache();
        var second = new WrapCache();
        var font = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 24f);
        const string text = "one two three four five six seven eight nine ten";

        var a = first.GetOrWrap(text, font, 24f, 100f);
        var b = second.GetOrWrap(text, font, 24f, 100f);

        Assert.AreNotSame(a, b, "each widget owns its own cache slot");
        Assert.AreEqual(a.Count, b.Count, "both wrap the same text identically");
    }

    [TestMethod]
    public void WrapCache_MultipleTexts_EachHitsOnSecondCall()
    {
        // The Twitch chat renderer wraps every visible message per frame — a
        // single-slot cache would evict message N-1 on message N and re-wrap
        // the whole chat every frame. Each distinct text must hit on its
        // second call, like the single-slot cache did for one text.
        var cache = new WrapCache();
        var font = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 24f);
        const string alpha = "alpha beta gamma delta epsilon zeta";
        const string beta = "one two three four five six seven eight";
        const string gamma = "lorem ipsum dolor sit amet consectetur";

        var alpha1 = cache.GetOrWrap(alpha, font, 24f, 100f);
        var beta1 = cache.GetOrWrap(beta, font, 24f, 100f);
        var gamma1 = cache.GetOrWrap(gamma, font, 24f, 100f);

        Assert.AreSame(alpha1, cache.GetOrWrap(alpha, font, 24f, 100f), "each distinct text must hit on its second call");
        Assert.AreSame(beta1, cache.GetOrWrap(beta, font, 24f, 100f), "each distinct text must hit on its second call");
        Assert.AreSame(gamma1, cache.GetOrWrap(gamma, font, 24f, 100f), "each distinct text must hit on its second call");
    }

    [TestMethod]
    public void WrapCache_Bounded_EvictsLeastRecentlyUsed()
    {
        var cache = new WrapCache(capacity: 2);
        var font = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 24f);

        var first = cache.GetOrWrap("first text", font, 24f, 100f);
        var second = cache.GetOrWrap("second text", font, 24f, 100f);
        var third = cache.GetOrWrap("third text", font, 24f, 100f);

        Assert.AreSame(second, cache.GetOrWrap("second text", font, 24f, 100f), "the live entries must still hit");
        Assert.AreSame(third, cache.GetOrWrap("third text", font, 24f, 100f), "the live entries must still hit");
        Assert.AreNotSame(first, cache.GetOrWrap("first text", font, 24f, 100f), "the evicted entry must re-wrap");
    }
}
