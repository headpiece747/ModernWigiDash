using System.IO;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's one post-mutation contract (<c>ApplyProfileMutation</c>) pinned
/// through a live window on STA, plus the window's delete seam and its dirty-mark
/// reach into the profile file.
///
/// <para>
/// The contract's BEHAVIOUR - which refresh bundle each shape applies, one dirty
/// mark per mutation - belongs to <see cref="ProfileMutationFunnel"/> and is
/// pinned by <see cref="ProfileMutationFunnelTests"/> with no window at all. What
/// only a window can show is the BINDING: that each bundle member lands on the
/// control the window means by it (the tab strip is the real
/// <c>PanelPageTabs</c>, the snap resync is the real <c>ChkSnapToGrid</c>). That
/// is two controls across three shapes - six facts - and it was spread over three
/// separate windows, one shape each, when all six fit in one (the architecture
/// review's "wire nothing" seam candidate, 2026-09-27).
/// </para>
///
/// The delete seam's stale-index rule is pinned at the module level
/// (<c>PageManagementTests.Delete_StaleIndex_IsANoOpNotAThrow</c>, which asserts
/// the <c>StaleIndex</c> verdict). What needs the window is that the window
/// PASSES the index through instead of indexing the page list itself first -
/// which is why it rides along in the consolidated test rather than paying for a
/// window of its own.
/// </summary>
[TestClass]
public class ProfileMutationContractTests
{
    private static readonly StaHost Host = new("ProfileMutationContract-STA");

    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }

    [TestMethod]
    public void EveryShape_RoutesItsRefreshBundleToTheRealControls_AndTheDeleteSeamPassesAStaleIndexThrough()
    {
        var (_, error) = Host.Invoke(() =>
        {
            var window = NewWindow();
            try
            {
                // ORDER IS LOADING-BEARING, and the reason is worth stating.
                // RawWrite runs FIRST, and it runs with the PROFILE diverged from
                // the CONTROL - profile snap false, control still true from
                // construction. That divergence has to be planted in the profile,
                // not in the checkbox: the checkbox's own write-back handler
                // routes a control-side change through the Transform shape into
                // the profile, so touching the control would re-sync the two and
                // leave nothing to observe.
                //
                // This is also what makes the assertion real. The previous form
                // ("a raw write re-syncs the snap toggle", asserted while the
                // control already held true) passed whether or not the binding
                // ran at all - turning SetSnapToGridFromHandler into a no-op left
                // the mutation green.
                window.Profile.ActivePage.SnapToGrid = false;
                var firstTab = window.PanelPageTabs.Children[0];
                window.ApplyProfileMutation(ProfileMutationShape.RawWrite, null);
                Assert.AreNotSame(firstTab, window.PanelPageTabs.Children[0],
                    "a raw write rebuilds the tab strip from the (imported) profile");
                Assert.IsFalse(window.ChkSnapToGrid.IsChecked == true,
                    "a raw write re-syncs the snap toggle FROM THE PROFILE - the control was true and the profile false, so only the binding can explain it");

                // Diverge the page-level toggle from the profile state: from here
                // the toggle is false and every shape must leave it alone.
                window.ChkSnapToGrid.IsChecked = false;

                firstTab = window.PanelPageTabs.Children[0];
                window.ApplyProfileMutation(ProfileMutationShape.Structural, null);
                Assert.AreNotSame(firstTab, window.PanelPageTabs.Children[0],
                    "a structural mutation re-syncs the tab strip from the profile");
                Assert.IsFalse(window.ChkSnapToGrid.IsChecked == true,
                    "a structural mutation never resyncs the snap toggle - only RawWrite does");

                firstTab = window.PanelPageTabs.Children[0];
                window.ApplyProfileMutation(ProfileMutationShape.Transform, null);
                Assert.AreSame(firstTab, window.PanelPageTabs.Children[0],
                    "a transform (in-page) mutation never rebuilds the tab strip");
                Assert.IsFalse(window.ChkSnapToGrid.IsChecked == true,
                    "a transform (in-page) mutation never touches the snap toggle");

                // The delete seam passes a stale index straight through to the
                // module instead of indexing the page list first (the old shape
                // threw here, ahead of the module's own validation). Free here:
                // the window is already up.
                int tabsBefore = window.PanelPageTabs.Children.Count;
                window.DeletePage(99); // stale: beyond any real profile
                window.DeletePage(-1); // stale: below the list
                Assert.AreEqual(tabsBefore, window.PanelPageTabs.Children.Count,
                    "a stale index must leave the page set untouched");
            }
            finally
            {
                window.Close();
            }
            return null;
        });
        Assert.IsNull(error, error?.ToString());
    }

    [TestMethod]
    public async Task InPageMutation_SingleDirtyMark_ReachesTheProfileFile()
    {
        // End to end: a shape's ONE MarkDirty arms the debounce and the
        // mutated state lands in profile.json — the window owns no save
        // logic; the persistence module owns the write. The wait runs on the
        // test thread (the debounce saves on a thread-pool continuation, so
        // nothing the window does needs STA while we poll the file).
        string profilePath = Path.Combine(Path.GetTempPath(), "wmd-mutant-" + Guid.NewGuid().ToString("N"), "profile.json");
        var (buildResult, buildError) = Host.Invoke(() =>
        {
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), UsbEngine: FakeTransport.InertEngine()));
            // The window's constructor persisted the starter profile
            // (snap ON) on first launch.
            Assert.IsTrue(File.ReadAllText(profilePath).Contains("\"SnapToGrid\": true"),
                "precondition: the starter profile is persisted with snap ON");

            // Toggling the checkbox routes through its handler into the
            // contract's Transform shape — the one window-driven mark.
            window.ChkSnapToGrid.IsChecked = false;
            return (object?)window;
        });
        Assert.IsNull(buildError, buildError?.ToString());
        if (buildResult is not MainWindow window) return;

        try
        {
            await TestWait.WaitUntilAsync(
                () => File.ReadAllText(profilePath).Contains("\"SnapToGrid\": false"),
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            // A throwing Close must fail the test, not leak the window: the
            // leaked window's live telemetry loops would keep writing the
            // shared sensor store for the rest of the process.
            var (_, closeError) = Host.Invoke(() => { window.Close(); return null; });
            Assert.IsNull(closeError, closeError?.ToString());
        }
    }

    private static MainWindow NewWindow()
        => new MainWindow(new MainWindowTestOptions(
            new StubPresentMonNative(),
            Path.Combine(Path.GetTempPath(), "wmd-mutant-" + Guid.NewGuid().ToString("N"), "profile.json"),
            new NoopPowerModeSource(),
            new FakeTraySurface(),
            UsbEngine: FakeTransport.InertEngine()));
}
