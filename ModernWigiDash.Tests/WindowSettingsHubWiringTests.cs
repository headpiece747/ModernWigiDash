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
/// That is what makes the claim cheap to state: a commit can only land if the
/// provider DEFERS to the field. <c>SettingsCommitModuleTests</c> already pins
/// the module against its own fakes - the module is fully module-tested and
/// needs nothing from this class - but the window's own argument list was
/// pinned by nothing at all. Measured, not assumed: rewriting
/// <c>profileProvider: () =&gt; _profile</c> into an eager capture at the wiring
/// site left all 2,561 tests green, because the eagerly captured value is null
/// and no test drove a commit through the window at all.
///
/// Two tests, because there are two failure shapes and only one of them
/// throws. The first is the loud half: at the current wiring site the profile
/// is null, so an eager capture is a NullReferenceException. The second is the
/// SILENT half: after an import has REPLACED the profile object, a
/// stale-but-non-null capture commits to the profile the window no longer uses,
/// and nothing throws. The swap runs through the window's internal test seam
/// (<c>SwapProfileForTest</c>, a forward into the import host adapter - the
/// flow's production host, not the window itself), so the test drives the
/// same production path the import flow does, and no production seam change
/// was needed for either test.
///
/// STA DISCIPLINE, learned the hard way writing the second test: the swap
/// rebuilds the tab strip, so it touches WPF controls and MUST run on the
/// window's host thread. A commit only assigns a field, which is why the first
/// test can call it from the test thread. Calling the swap from the test
/// thread produced a WPF failure that the window's own error dialog then
/// masked with a second failure, so the real cause was invisible in the test
/// output - which is why every WPF-touching step here is inside
/// <c>Host.Invoke</c> and only the file read is on the test thread.
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

    [TestMethod]
    public async Task ACommitAfterAProfileSwap_ReachesTheNewProfile_NotTheOneItReplaced()
    {
        // The close behavior vocabulary has two values ("quit", "hideToTray"),
        // so the two profiles are told apart by what each one HELD before the
        // commit, not by the committed value: the replaced profile starts at
        // Quit, the imported one has no opinion, and only the provider's target
        // decides where the committed value lands.
        string profilePath = Path.Combine(
            Path.GetTempPath(), "wmd-hubwire-swap-" + Guid.NewGuid().ToString("N"), "profile.json");

        var (legResult, buildError) = Host.Invoke(() =>
        {
            var window = new MainWindow(new MainWindowTestOptions(
                new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(),
                UsbEngine: FakeTransport.InertEngine()));

            // ReplaceProfile disposes the old profile's widgets and RETURNS the
            // imported one, so the local stays the live profile and the old
            // object stays readable - which is what makes the two
            // distinguishable after the swap.
            ProfileLayout replaced = window.Profile;
            var imported = new ProfileLayout { CloseBehavior = null };

            // A commit BEFORE the swap, which is the app's real order: the user
            // changes a setting, imports a profile, changes another. It is also
            // the only thing that makes a memoized provider visible - a
            // provider that captures on FIRST use and never re-reads caches
            // the pre-swap profile here, and the post-swap commit then lands on
            // the dead one. With the swap first and the commit second, that
            // mutation captures the NEW profile and looks correct, which is
            // exactly what the first version of this test did and why it passed
            // a stale provider.
            ((ISettingsHubHost)window).CommitCloseBehavior(CloseBehaviorPolicy.Quit);
            string? replacedAfterFirstCommit = replaced.CloseBehavior;

            bool swapped = window.SwapProfileForTest(imported);
            bool liveIsImported = ReferenceEquals(window.Profile, imported);
            ((ISettingsHubHost)window).CommitCloseBehavior(CloseBehaviorPolicy.HideToTray);

            return (object?)new SwapLeg(window, replaced, imported, replacedAfterFirstCommit, swapped, liveIsImported);
        });
        Assert.IsNull(buildError, buildError?.ToString());
        if (legResult is not SwapLeg leg) return;

        try
        {
            Assert.IsTrue(leg.Swapped, "the profile swap must succeed for this claim to mean anything");
            Assert.IsTrue(leg.LiveIsImported,
                "precondition: the imported profile is the live one after the swap");
            Assert.AreEqual(CloseBehaviorPolicy.Quit, leg.ReplacedAfterFirstCommit,
                "precondition: the commit BEFORE the swap landed on the pre-swap profile");
            Assert.AreEqual(CloseBehaviorPolicy.HideToTray, leg.Imported.CloseBehavior,
                "the provider must re-read the window's profile, so a commit after the swap lands on the profile now in charge");
            Assert.AreEqual(CloseBehaviorPolicy.Quit, leg.Replaced.CloseBehavior,
                "and must NOT land on the profile the swap replaced - a provider that captured it before the swap would overwrite this instead, and nothing would throw");

            // The same debounce wait the first test uses, and for the same
            // reason: the commit's dirty mark is a 2 s debounce, so a window
            // closed before it fires has its pending save CANCELLED by
            // Dispose, and the only thing on disk is the starter profile the
            // constructor persisted - which carries no close behavior at all.
            // Reading the file right after a close is therefore a test of
            // nothing; waiting is what makes it a test of the write-through.
            await TestWait.WaitUntilAsync(
                () => File.ReadAllText(profilePath).Contains($"\"{CloseBehaviorPolicy.HideToTray}\""),
                TimeSpan.FromSeconds(10));
        }
        finally
        {
            var (_, closeError) = Host.Invoke(() => { leg.Window.Close(); return null; });
            Assert.IsNull(closeError, closeError?.ToString());
        }
    }

    /// <summary>What the swap leg observed. The window is carried so the test
    /// closes it on its own host thread, and the two profile references so no
    /// assertion runs inside the host invoke.</summary>
    private sealed record SwapLeg(
        MainWindow Window,
        ProfileLayout Replaced,
        ProfileLayout Imported,
        string? ReplacedAfterFirstCommit,
        bool Swapped,
        bool LiveIsImported);
}
