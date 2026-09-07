namespace ModernWigiDash.Tests;

/// <summary>
/// The profile-mutation funnel pinned at its module interface, without a
/// window or an STA pump: each shape applies exactly its refresh bundle (the
/// tab strip rebuilds only for Structural/RawWrite, the snap-toggle resync only
/// for RawWrite), every shape ends in exactly one dirty mark and one hotkey
/// refresh, and the selection re-application routes through the binding. The
/// window-level ProfileMutationContractTests still pin the XAML forwarding;
/// these pin the policy itself.
/// </summary>
[TestClass]
public class ProfileMutationFunnelTests
{
    private sealed class RecordingState
    {
        public int RebuildCount;
        public bool? LastSnapValue;
        public int SnapResyncCount;
        public int CountWrites;
        public int RepaintCount;
        public int DirtyMarks;
        public int HotkeyRefreshes;
        public PlacedWidgetInstance? LastSelection;
        public int SelectionApplies;
    }

    private static (ProfileMutationFunnel Funnel, RecordingState State) NewFunnel()
    {
        var s = new RecordingState();
        var bindings = new ProfileMutationBindings(
            RebuildPageTabs: _ => s.RebuildCount++,
            SetSnapToGridFromHandler: v => { s.LastSnapValue = v; s.SnapResyncCount++; },
            WriteActiveCountText: () => s.CountWrites++,
            RequestCanvasRepaint: () => s.RepaintCount++,
            MarkDirty: () => s.DirtyMarks++,
            RefreshGlobalHotkeys: () => s.HotkeyRefreshes++,
            ApplySelection: w => { s.LastSelection = w; s.SelectionApplies++; });
        return (new ProfileMutationFunnel(bindings), s);
    }

    private static ProfileLayout NewProfile(bool snapOn = true)
    {
        var page = new PageLayout { PageName = "P", SnapToGrid = snapOn };
        return new ProfileLayout { Pages = [page], ActivePageIndex = 0 };
    }

    [TestMethod]
    public void Structural_RebuildsTabs_SkipsSnapResync_OneDirtyMark()
    {
        var (funnel, s) = NewFunnel();

        funnel.Apply(ProfileMutationShape.Structural, NewProfile(), null);

        Assert.AreEqual(1, s.RebuildCount, "a structural mutation rebuilds the tab strip");
        Assert.AreEqual(0, s.SnapResyncCount, "a structural mutation never resyncs the snap toggle");
        Assert.AreEqual(1, s.DirtyMarks, "exactly one dirty mark");
        Assert.AreEqual(1, s.HotkeyRefreshes, "one hotkey refresh");
        Assert.AreEqual(1, s.CountWrites, "the active count is refreshed");
        Assert.AreEqual(1, s.RepaintCount, "the canvas repaints");
    }

    [TestMethod]
    public void Transform_TouchesNoStructuralControls_OneDirtyMark()
    {
        var (funnel, s) = NewFunnel();

        funnel.Apply(ProfileMutationShape.Transform, NewProfile(), null);

        Assert.AreEqual(0, s.RebuildCount, "a transform mutation never rebuilds the tab strip");
        Assert.AreEqual(0, s.SnapResyncCount, "a transform mutation never touches the snap toggle");
        Assert.AreEqual(1, s.DirtyMarks, "exactly one dirty mark");
        Assert.AreEqual(1, s.HotkeyRefreshes, "one hotkey refresh");
    }

    [TestMethod]
    public void RawWrite_RebuildsTabs_And_ResyncsSnapFromTheProfile()
    {
        var (funnel, s) = NewFunnel();

        funnel.Apply(ProfileMutationShape.RawWrite, NewProfile(snapOn: false), null);

        Assert.AreEqual(1, s.RebuildCount, "a raw write rebuilds the tab strip");
        Assert.AreEqual(1, s.SnapResyncCount, "a raw write resyncs the snap toggle");
        Assert.IsFalse(s.LastSnapValue, "the resync reads the imported page's snap value");
        Assert.AreEqual(1, s.DirtyMarks, "exactly one dirty mark");
    }

    [TestMethod]
    public void EveryShape_RoutesTheSelectionThroughTheBinding()
    {
        foreach (var shape in new[] { ProfileMutationShape.Transform, ProfileMutationShape.Structural, ProfileMutationShape.RawWrite })
        {
            var (funnel, s) = NewFunnel();
            var widget = new PlacedWidgetInstance();

            funnel.Apply(shape, NewProfile(), widget);

            Assert.AreSame(widget, s.LastSelection, $"{shape} re-applies the selection");
            Assert.AreEqual(1, s.SelectionApplies, $"{shape} applies the selection exactly once");
        }
    }
}
