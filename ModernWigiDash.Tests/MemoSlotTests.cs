namespace ModernWigiDash.Tests;

/// <summary>The single-slot memo pattern the widgets' hand-rolled caches now
/// share — key equality, compute-once, recompute-on-change, through the
/// closure-free <c>TryGet</c>/<c>Set</c> path (a hit allocates nothing).</summary>
[TestClass]
public class MemoSlotTests
{
    [TestMethod]
    public void TryGet_Set_SameKey_ComputesOnce()
    {
        var slot = new MemoSlot<string, string>();
        int computes = 0;

        string first = Drive(slot, "k", ref computes, "v");
        string second = Drive(slot, "k", ref computes, "v");

        Assert.AreEqual("v", first);
        Assert.AreEqual("v", second);
        Assert.AreEqual(1, computes, "an unchanged key must reuse the cached value");
    }

    [TestMethod]
    public void TryGet_Set_NewKey_Recomputes()
    {
        var slot = new MemoSlot<string, string>();
        int computes = 0;

        _ = Drive(slot, "a", ref computes, "1");
        _ = Drive(slot, "b", ref computes, "2");

        Assert.AreEqual(2, computes);
    }

    [TestMethod]
    public void TryGet_Set_TupleKey_ValueEqualityReuses()
    {
        var slot = new MemoSlot<(int, string), string>();
        int computes = 0;

        _ = Drive(slot, (1, "x"), ref computes, "v");
        _ = Drive(slot, (1, "x"), ref computes, "v");
        _ = Drive(slot, (1, "y"), ref computes, "v2");

        Assert.AreEqual(2, computes, "equal tuples reuse; a differing element recomputes");
    }

    [TestMethod]
    public void TryGet_Set_ToleranceComparer_HitsWithinEpsilon()
    {
        var slot = new MemoSlot<float, string>(new FloatToleranceComparer(1e-3f));
        int computes = 0;

        _ = Drive(slot, 1.0f, ref computes, "v");
        _ = Drive(slot, 1.0005f, ref computes, "v2");
        _ = Drive(slot, 1.5f, ref computes, "v3");

        Assert.AreEqual(2, computes, "a key within the tolerance reuses; beyond it recomputes");
    }

    [TestMethod]
    public void Invalidate_NextCallRecomputesAndLastValueIsDefault()
    {
        var slot = new MemoSlot<string, string>();
        int computes = 0;

        _ = Drive(slot, "k", ref computes, "v");
        slot.Invalidate();
        string after = Drive(slot, "k", ref computes, "v2");

        Assert.AreEqual("v2", after, "an invalidated slot recomputes even for the last key");
        Assert.AreEqual(2, computes);
    }

    /// <summary>Drives the closure-free path: a hit serves the cache, a miss
    /// computes (counted) and stores.</summary>
    private static TValue Drive<TKey, TValue>(MemoSlot<TKey, TValue> slot, TKey key, ref int computes, TValue value)
    {
        if (slot.TryGet(key, out TValue v))
        {
            return v;
        }

        computes++;
        slot.Set(key, value);
        return value;
    }

    /// <summary>The tolerance-key shape (the Steam grid's sub-pixel scroll epsilon).</summary>
    private sealed class FloatToleranceComparer(float epsilon) : IEqualityComparer<float>
    {
        public bool Equals(float x, float y) => Math.Abs(x - y) < epsilon;

        public int GetHashCode(float obj) => obj.GetHashCode();
    }
}
