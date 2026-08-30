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

    /// <summary>A live widget that offers a global hotkey: both
    /// <see cref="IModernWidget"/> (the shape of
    /// <c>PlacedWidgetInstance.ActiveInstance</c>) and the provider
    /// capability.</summary>
    private sealed class FakeHotkeyWidget : IModernWidget, IGlobalHotkeyProvider
    {
        private readonly int _modFlags;
        private readonly ushort _virtualKey;
        private readonly string _chord;
        private readonly bool _offered;

        public FakeHotkeyWidget(int modFlags, ushort virtualKey, string chord, bool offered)
        {
            _modFlags = modFlags;
            _virtualKey = virtualKey;
            _chord = chord;
            _offered = offered;
        }

        public string InstanceId { get; set; } = "fake-hotkey";
        public SKSize DefaultSize => new(10, 10);
        public ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default) => default;
        public void Render(SKCanvas canvas, SKRect bounds) { }
        public void OnTouch(SKPoint localPoint, TouchEventType eventType) { }
        public void OnPropertyChanged(string propertyName, object? newValue) { }
        public ValueTask DisposeAsync() => default;
        public bool TryGetGlobalHotkey(out int modFlags, out ushort virtualKey, out string chord)
        {
            modFlags = _modFlags;
            virtualKey = _virtualKey;
            chord = _chord;
            return _offered;
        }
        public void FireGlobalHotkey() { }
    }

    [TestMethod]
    public void CollectCandidates_ProfileOrder_CollectsOnlyTheOfferingProviders()
    {
        var offered = new FakeHotkeyWidget(0x2, (ushort)'A', "Ctrl+A", offered: true);
        var declined = new FakeHotkeyWidget(0x4, (ushort)'B', "Shift+B", offered: false);
        var pageTwo = new FakeHotkeyWidget(0x1, (ushort)'C', "Alt+C", offered: true);

        var profile = new ProfileLayout();
        profile.Pages[0].Widgets.Add(new PlacedWidgetInstance { ActiveInstance = offered });
        profile.Pages[0].Widgets.Add(new PlacedWidgetInstance { ActiveInstance = declined });
        profile.Pages[0].Widgets.Add(new PlacedWidgetInstance());
        profile.Pages[0].Widgets.Add(new PlacedWidgetInstance { ActiveInstance = new StopwatchTimerWidget() });
        profile.Pages.Add(new PageLayout());
        profile.Pages[1].Widgets.Add(new PlacedWidgetInstance { ActiveInstance = pageTwo });

        var candidates = GlobalHotkeyPolicy.CollectCandidates(profile);

        Assert.AreEqual(2, candidates.Count,
            "the declined provider, the instance-less placement, and the non-provider widget contribute nothing");
        Assert.AreSame(offered, candidates[0].Owner, "page one's offering widget is first (profile order)");
        Assert.AreSame(pageTwo, candidates[1].Owner, "page two's offering widget is second (pages are walked in order)");
        Assert.AreEqual(0x2, candidates[0].ModFlags);
        Assert.AreEqual((ushort)'A', candidates[0].VirtualKey);
        Assert.AreEqual("Ctrl+A", candidates[0].Chord);
    }

    [TestMethod]
    public void CollectCandidates_NullProfile_CollectsNothing()
    {
        Assert.AreEqual(0, GlobalHotkeyPolicy.CollectCandidates(null).Count,
            "a pre-profile window collects no candidates (the benign no-op)");
    }
}
