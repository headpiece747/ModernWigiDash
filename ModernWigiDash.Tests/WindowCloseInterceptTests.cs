using System.IO;
using System.Windows;
using System.Windows.Threading;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's close intercept pinned on a live STA window (ADR-0018): a
/// close with the tray keep-alive behavior hides instead of closing (and
/// the tray's QuitClose still exits through the veto), a minimize hides
/// under the same policy, a quit-behavior close runs the normal sequence,
/// and the session-end standby routes to the engine's seam. The tray
/// surface is the injected live fake: the production surface reads dead in
/// the test host (its icon resource is not in the test output dir), and the
/// N1 fallback would swallow every hide.
/// </summary>
[TestClass]
public class WindowCloseInterceptTests
{
    private static readonly StaHost Host = new("WindowCloseIntercept-STA");

    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }

    [TestMethod]
    public void Close_WithHideToTrayBehavior_HidesInsteadOfClosing_AndQuitCloseStillExits()
    {
        bool closedFired = false;
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        Host.Run<object?>(() =>
        {
            var fake = new FakeTraySurface();
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake);
            try
            {
                window.Show();
                window.Closed += (_, _) => closedFired = true;

                window.Close();

                Assert.IsFalse(closedFired, "the intercept cancels the close - the teardown must not run for a hidden window");
                Assert.IsFalse(window.IsVisible, "the window hides to the tray instead of closing");
                Assert.IsTrue(fake.IsLive, "the hide decision read the live tray");

                // The tray's QuitClose still exits: the veto flag makes the
                // close proceed through the normal sequence.
                window.QuitClose();
                Assert.IsTrue(closedFired, "the explicit-quit veto bypasses the intercept");
            }
            finally
            {
                if (!closedFired)
                {
                    window.QuitClose();
                }
            }
            return null;
        });
    }

    [TestMethod]
    public void Minimize_WithHideToTrayBehavior_HidesInsteadOfMinimizing()
    {
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        Host.Run<object?>(() =>
        {
            var fake = new FakeTraySurface();
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake);
            try
            {
                window.Show();
                window.UpdateLayout();

                window.WindowState = WindowState.Minimized;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                Assert.IsFalse(window.IsVisible, "the minimize hides to the tray instead of minimizing");
            }
            finally
            {
                window.QuitClose();
            }
            return null;
        });
    }

    [TestMethod]
    public void Close_WithQuitBehavior_RunsTheNormalCloseSequence()
    {
        bool closedFired = false;
        string profilePath = SeedProfile(null); // absent: the default quit
        Host.Run<object?>(() =>
        {
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface());
            window.Show();
            window.Closed += (_, _) => closedFired = true;
            try
            {
                window.Close();

                Assert.IsTrue(closedFired, "with the pre-feature behavior the close runs the normal sequence (the teardown)");
                Assert.IsFalse(window.IsVisible);
            }
            finally
            {
                if (!closedFired)
                {
                    window.QuitClose();
                }
            }
            return null;
        });
    }

    [TestMethod]
    public void SessionEndStandby_RoutesThroughTheSeam_AndReturnsTheVerdict()
    {
        int calls = 0;
        string profilePath = SeedProfile(null);
        Host.Run<object?>(() =>
        {
            var window = new MainWindow(
                new StubPresentMonNative(),
                profilePath,
                new NoopPowerModeSource(),
                new FakeTraySurface(),
                () => { calls++; return true; });
            try
            {
                Assert.IsTrue(window.RunSessionEndStandby(), "the probe's verdict rides back through the seam");
                Assert.AreEqual(1, calls, "the routing calls the seam exactly once per session end");
            }
            finally
            {
                window.QuitClose();
            }
            return null;
        });
    }

    /// <summary>Seeds a one-page profile with the given raw close-behavior
    /// value (null writes the field absent) so the window's profile load
    /// picks it up the production way.</summary>
    private static string SeedProfile(string? closeBehavior)
    {
        string dir = Path.Combine(Path.GetTempPath(), "wmd-close-intercept-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string profilePath = Path.Combine(dir, "profile.json");
        string field = closeBehavior is null ? "" : $",\"CloseBehavior\":\"{closeBehavior}\"";
        File.WriteAllText(profilePath, $"{{\"ProfileId\":\"test\",\"Pages\":[{{\"PageName\":\"A\"}}]{field}}}");
        return profilePath;
    }
}
