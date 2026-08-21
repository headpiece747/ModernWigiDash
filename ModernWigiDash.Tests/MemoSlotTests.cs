
namespace ModernWigiDash.Tests;

/// <summary>The single-slot memo pattern the widgets' hand-rolled caches now
/// share — key equality, compute-once, recompute-on-change.</summary>
[TestClass]
public class MemoSlotTests
{
    [TestMethod]
    public void GetOrCompute_SameKey_ComputesOnce()
    {
        var slot = new MemoSlot<string, string>();
        int computes = 0;

        string first = slot.GetOrCompute("k", () => { computes++; return "v"; });
        string second = slot.GetOrCompute("k", () => { computes++; return "v"; });

        Assert.AreEqual("v", first);
        Assert.AreEqual("v", second);
        Assert.AreEqual(1, computes, "an unchanged key must reuse the cached value");
    }

    [TestMethod]
    public void GetOrCompute_NewKey_Recomputes()
    {
        var slot = new MemoSlot<string, string>();
        int computes = 0;

        _ = slot.GetOrCompute("a", () => { computes++; return "1"; });
        _ = slot.GetOrCompute("b", () => { computes++; return "2"; });

        Assert.AreEqual(2, computes);
    }

    [TestMethod]
    public void GetOrCompute_TupleKey_ValueEqualityReuses()
    {
        var slot = new MemoSlot<(int, string), string>();
        int computes = 0;

        _ = slot.GetOrCompute((1, "x"), () => { computes++; return "v"; });
        _ = slot.GetOrCompute((1, "x"), () => { computes++; return "v"; });
        _ = slot.GetOrCompute((1, "y"), () => { computes++; return "v2"; });

        Assert.AreEqual(2, computes, "equal tuples reuse; a differing element recomputes");
    }
}
