using System.IO;
using ModernWigiDash.App;
using ModernWigiDash.App.Power;
using ModernWigiDash.Core.Models;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's commit wiring, pinned at the one place it is visible: the
/// window constructs <c>SettingsCommitModule</c> with providers that close over
/// its own fields, and the profile those providers read does not EXIST YET when
/// they are wired (the field is <c>null!</c> until the startup profile load).
///
/// That is what makes the claim here cheap to state and impossible to state
/// without a window: a commit can only land if the provider DEFERS to the
/// field. <c>SettingsCommitModuleTests</c> already pins the module against its
/// own fakes - the module is fully module-tested and needs nothing from this
/// class - but the window's own argument list was pinned by nothing at all.
/// Measured, not assumed: rewriting <c>profileProvider: () =&gt; _profile</c>
/// into an eager capture at the wiring site left all 2,561 tests green, because
/// the eagerly captured value is null and no test drove a commit through the
/// window at all. This class is that test (the architecture review's
/// "MainWindow module wiring is untestable" candidate, 2026-09-27).
///
/// One window, both claims it can show: the commit reaches the live profile,
/// and the same commit's dirty mark carries it to profile.json. The narrower
/// residual gap - a commit AFTER an import REPLACES the profile object, where a
/// stale-but-non-null capture would fail silently rather than loudly - needs
/// the import seam to reach and is recorded there rather than faked here.
/// </summary>
[TestClass]
public class WindowSettingsHubWiringTests
{
    private static readonly StaHost Host = new("WindowSettingsHubWiring-STA");

    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }

    [TestMethod]
    public async Task ACommitThroughTheHubSeam_ReachesTheProfileLoadedAfterTheWiring_AndReachesDisk()
    {
        string profilePath = Path.Combine(
            Path.GetTempPath(), "wmd-hubwire-" + Guid.NewGuid().ToString("N"), "profile.json");

        var (buildResult, buildError) = Host.Invoke(() =>
        {
            var built = new MainWindow(new MainWindowTestOptions(
                new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(),
                UsbEngine: FakeTransport.InertEngine()));
            return (object?)built;
        });
        Assert.IsNull(buildError, buildError?.ToString());
        if (buildResult is not MainWindow app) return;

        try
        {
            // The starter profile has no opinion on close behavior, so the
            // commit's value is the only thing that can produce it below.
            Assert.IsNull(app.Profile.CloseBehavior,
                "precondition: the starter profile leaves close behavior unset, so a landed commit is unambiguous");

            // The hub's own typed seam - the same one the settings dialog
            // crosses, not the module behind it.
            var host = (ISettingsHubHost)app;
            host.CommitCloseBehavior(CloseBehaviorPolicy.HideToTray);

            Assert.AreEqual(CloseBehaviorPolicy.HideToTray, app.Profile.CloseBehavior,
                "the commit module's profile provider must READ the window's current profile, not a value captured when the module was wired - the profile did not exist yet at wiring time");

            // The same commit's dirty mark carries it to disk through the
            // debounce. The wait runs on the test thread: the debounce saves on
            // a thread-pool continuation, so nothing here needs STA.
            await TestWait.WaitUntilAsync(
                () => File.ReadAllText(profilePath).Contains($"\"{CloseBehaviorPolicy.HideToTray}\""),
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            // A throwing Close must fail the test, not leak the window: the
            // leaked window's live telemetry loops keep writing the shared
            // sensor store for the rest of the process.
            var (_, closeError) = Host.Invoke(() => { app.Close(); return null; });
            Assert.IsNull(closeError, closeError?.ToString());
        }
    }
}
