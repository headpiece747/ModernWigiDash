namespace ModernWigiDash.Tests;

/// <summary>
/// The close-behavior vocabulary and parse rule pinned without a window: the
/// two known spellings, the exact-match rule (case is identity), and the
/// default degradation for null and unknown values (a hand-edited profile can
/// never smuggle in a behavior).
/// </summary>
[TestClass]
public class CloseBehaviorPolicyTests
{
    [TestMethod]
    public void IsKnown_BothKnownSpellings_AreKnown()
    {
        Assert.IsTrue(CloseBehaviorPolicy.IsKnown(CloseBehaviorPolicy.Quit), "'quit' is a known spelling");
        Assert.IsTrue(CloseBehaviorPolicy.IsKnown(CloseBehaviorPolicy.HideToTray), "'hideToTray' is a known spelling");
    }

    [TestMethod]
    public void IsKnown_NullBlankUnknownOrDifferentCase_AreUnknown()
    {
        Assert.IsFalse(CloseBehaviorPolicy.IsKnown(null), "absent is not a known value");
        Assert.IsFalse(CloseBehaviorPolicy.IsKnown(""));
        Assert.IsFalse(CloseBehaviorPolicy.IsKnown("   "));
        Assert.IsFalse(CloseBehaviorPolicy.IsKnown("QUIT"), "case is identity");
        Assert.IsFalse(CloseBehaviorPolicy.IsKnown("HideToTray"), "case is identity");
        Assert.IsFalse(CloseBehaviorPolicy.IsKnown("tray"), "an unknown future value is unknown");
    }

    [TestMethod]
    public void Resolve_KnownValue_ReturnsThatValue()
    {
        Assert.AreEqual(CloseBehaviorPolicy.Quit, CloseBehaviorPolicy.Resolve(CloseBehaviorPolicy.Quit));
        Assert.AreEqual(CloseBehaviorPolicy.HideToTray, CloseBehaviorPolicy.Resolve(CloseBehaviorPolicy.HideToTray));
    }

    [TestMethod]
    public void Resolve_NullOrUnknown_ReturnsTheDefaultQuit()
    {
        Assert.AreEqual(CloseBehaviorPolicy.Quit, CloseBehaviorPolicy.Resolve(null), "absent is the default");
        Assert.AreEqual(CloseBehaviorPolicy.Quit, CloseBehaviorPolicy.Resolve("anything"), "unknown degrades to the default");
    }

    [TestMethod]
    public void Default_IsTheQuitSpelling()
    {
        Assert.AreEqual("quit", CloseBehaviorPolicy.Default, "the pre-feature behavior is a normal exit");
    }

    // ── the import merge (absent keeps local, present wins) ──────

    [TestMethod]
    public void MergeImport_ImportedAbsent_ReturnsTheLocalValue()
    {
        Assert.AreEqual(CloseBehaviorPolicy.Quit,
            CloseBehaviorPolicy.MergeImport(null, CloseBehaviorPolicy.Quit),
            "a local 'quit' must survive an import that has no opinion");
        Assert.AreEqual(CloseBehaviorPolicy.HideToTray,
            CloseBehaviorPolicy.MergeImport(null, CloseBehaviorPolicy.HideToTray),
            "a local 'hideToTray' must survive an import that has no opinion");
        Assert.AreEqual(CloseBehaviorPolicy.Quit,
            CloseBehaviorPolicy.MergeImport(null, null),
            "two profiles with no opinion land on the default quit");
    }

    [TestMethod]
    public void MergeImport_ImportedPresent_WinsOverLocal()
    {
        Assert.AreEqual(CloseBehaviorPolicy.HideToTray,
            CloseBehaviorPolicy.MergeImport(CloseBehaviorPolicy.HideToTray, CloseBehaviorPolicy.Quit),
            "a foreign 'hideToTray' overrides the local 'quit'");
        Assert.AreEqual(CloseBehaviorPolicy.Quit,
            CloseBehaviorPolicy.MergeImport(CloseBehaviorPolicy.Quit, CloseBehaviorPolicy.HideToTray),
            "a foreign 'quit' overrides the local 'hideToTray'");
    }

    [TestMethod]
    public void MergeImport_LocalCorrupt_StampedValueIsTheDefault()
    {
        // A hand-edited local file with an unknown value: the merge resolves
        // the local side, so the stamped value is a known spelling.
        Assert.AreEqual(CloseBehaviorPolicy.Quit,
            CloseBehaviorPolicy.MergeImport(null, "flyToTheMoon"),
            "a corrupt local value stamps the default, never the raw value");
    }
}
