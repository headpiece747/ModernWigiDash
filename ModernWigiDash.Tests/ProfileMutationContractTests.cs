using System.IO;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's one post-mutation contract (<c>ApplyProfileMutation</c>)
/// pinned through a live window on STA: each mutation shape applies exactly
/// its refresh bundle (no shape touches a control the contract says it
/// mustn't), and the window-driven dirty mark reaches the persisted profile
/// file through the debounce — no call site owns any save or mark logic of
/// its own anymore.
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
    public void Structural_RebuildsTheTabStrip_LeavesSnapToggleAlone()
    {
        var (_, error) = Host.Invoke(() =>
        {
            var window = NewWindow();
            try
            {
                // Diverge the page-level toggle from the profile state.
                window.ChkSnapToGrid.IsChecked = false;
                var firstTabBefore = window.PanelPageTabs.Children[0];

                window.ApplyProfileMutation(ProfileMutationShape.Structural, null);

                Assert.AreNotSame(firstTabBefore, window.PanelPageTabs.Children[0],
                    "a structural mutation re-syncs the tab strip from the profile");
                Assert.IsFalse(window.ChkSnapToGrid.IsChecked == true,
                    "a structural mutation never resyncs the snap toggle — only RawWrite does");
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
    public void Transform_TouchesNoStructuralControls()
    {
        var (_, error) = Host.Invoke(() =>
        {
            var window = NewWindow();
            try
            {
                // Diverge the page-level toggle from the profile state.
                window.ChkSnapToGrid.IsChecked = false;
                var firstTabBefore = window.PanelPageTabs.Children[0];

                window.ApplyProfileMutation(ProfileMutationShape.Transform, null);

                Assert.AreSame(firstTabBefore, window.PanelPageTabs.Children[0],
                    "a transform (in-page) mutation never rebuilds the tab strip");
                Assert.IsFalse(window.ChkSnapToGrid.IsChecked == true,
                    "a transform (in-page) mutation never touches the snap toggle");
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
    public void RawWrite_ResyncsPageControlsFromTheProfile()
    {
        // A raw write (an import) replaces the whole profile state: the
        // funnel re-syncs the page-level controls from the profile. The snap
        // toggle is already in sync after construction (its write-back handler
        // would land a divergence into the profile, so it cannot stage one),
        // so its resync is a no-op write here; the tab strip is rebuilt.
        var (_, error) = Host.Invoke(() =>
        {
            var window = NewWindow();
            try
            {
                var firstTabBefore = window.PanelPageTabs.Children[0];

                window.ApplyProfileMutation(ProfileMutationShape.RawWrite, null);

                Assert.AreNotSame(firstTabBefore, window.PanelPageTabs.Children[0],
                    "a raw write rebuilds the tab strip from the (imported) profile");
                Assert.IsTrue(window.ChkSnapToGrid.IsChecked == true,
                    "a raw write re-syncs the snap toggle from the (imported) page's state");
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

    [TestMethod]
    public void DeletePage_StaleIndex_IsANoOpNotAThrow()
    {
        // The window's delete seam must degrade a stale page index to a silent
        // no-op: the confirm's page facts read through the module's bounds-safe
        // accessor, never by the window indexing the page list first (the old
        // shape threw here, ahead of DeletePage's own validation).
        var (_, error) = Host.Invoke(() =>
        {
            var window = NewWindow();
            try
            {
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

    private static MainWindow NewWindow()
        => new MainWindow(new MainWindowTestOptions(
            new StubPresentMonNative(),
            Path.Combine(Path.GetTempPath(), "wmd-mutant-" + Guid.NewGuid().ToString("N"), "profile.json"),
            new NoopPowerModeSource(),
            new FakeTraySurface(),
            UsbEngine: FakeTransport.InertEngine()));
}
