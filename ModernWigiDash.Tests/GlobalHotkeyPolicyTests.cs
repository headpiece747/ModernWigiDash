using ModernWigiDash.App.Hotkey;

namespace ModernWigiDash.Tests;

/// <summary>
/// The global-hotkey policy's desired-set resolution pinned against the pure
/// module: the kill-switch veto (tripped drops every candidate), the
/// duplicate rule (the first widget in profile order wins a (flags, key)
/// cell; the later duplicate is dropped), and the distinct-cell passthrough.
/// </summary>
[TestClass]
public class GlobalHotkeyPolicyTests
{
    private sealed class FakeProvider : IGlobalHotkeyProvider
    {
        public int Fires { get; private set; }
        public bool TryGetGlobalHotkey(out int modFlags, out ushort virtualKey, out string chord)
        {
            modFlags = 0;
            virtualKey = 0;
            chord = "";
            return false;
        }
        public void FireGlobalHotkey() => Fires++;
    }

    private static DesiredGlobalHotkey Candidate(int flags, ushort vk, string chord)
        => new(flags, vk, chord, new FakeProvider());

    [TestMethod]
    public void ResolveDesired_FirstWidgetInProfileOrder_WinsTheDuplicateCell()
    {
        var first = Candidate(0x3, (ushort)'X', "Ctrl+Alt+X");
        var other = Candidate(0x2, (ushort)'Y', "Ctrl+Y");
        var duplicate = Candidate(0x3, (ushort)'X', "ctrl+alt+x"); // the same cell, later profile order

        var (desired, dropped) = GlobalHotkeyPolicy.ResolveDesired(false, [first, other, duplicate]);

        CollectionAssert.AreEqual(new[] { first, other }, desired.ToList(),
            "the profile order is preserved and the first widget wins the cell");
        CollectionAssert.AreEqual(new[] { duplicate }, dropped.ToList(),
            "the later duplicate is the one drop the window logs");
    }

    [TestMethod]
    public void ResolveDesired_KillSwitchTripped_DropsEveryCandidate()
    {
        var a = Candidate(0x2, (ushort)'A', "Ctrl+A");
        var b = Candidate(0x4, (ushort)'B', "Shift+B");

        var (desired, dropped) = GlobalHotkeyPolicy.ResolveDesired(true, [a, b]);

        Assert.AreEqual(0, desired.Count, "a tripped kill switch registers nothing");
        CollectionAssert.AreEqual(new[] { a, b }, dropped.ToList(),
            "the full candidate set is the dropped list (the window must not log it as duplicates)");
    }

    [TestMethod]
    public void ResolveDesired_DistinctCells_AllDesiredNoneDropped()
    {
        var a = Candidate(0x2, (ushort)'A', "Ctrl+A");
        var b = Candidate(0x1 | 0x8, (ushort)'B', "Alt+Win+B");
        var c = Candidate(0x2, (ushort)'C', "Ctrl+C");

        var (desired, dropped) = GlobalHotkeyPolicy.ResolveDesired(false, [a, b, c]);

        CollectionAssert.AreEqual(new[] { a, b, c }, desired.ToList());
        Assert.AreEqual(0, dropped.Count);
    }

    [TestMethod]
    public void ResolveDesired_EmptyCandidates_IsANoOp()
    {
        var (desired, dropped) = GlobalHotkeyPolicy.ResolveDesired(false, []);

        Assert.AreEqual(0, desired.Count);
        Assert.AreEqual(0, dropped.Count);
    }
}
