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
/// the N1 fallback (the tray dead) runs the normal close instead of
/// hiding into a void, a system-wide minimize with a modal dialog open
/// (the disabled owner) vetoes the hide, the tray show restores the
/// hidden window keeping the close leg's maximized state (and forcing
/// Normal back after the minimize leg), and the session-end standby
/// routes to the engine's seam. The tray surface is the injected live
/// fake: the production surface reads dead in the test host (its icon
/// resource is not in the test output dir), and the N1 fallback would
/// swallow every hide.
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
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, null, null, null, null, FakeTransport.InertEngine());
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
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, null, null, null, null, FakeTransport.InertEngine());
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
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), null, null, null, null, FakeTransport.InertEngine());
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
                () => { calls++; return true; },
                null, null, null, FakeTransport.InertEngine());
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

    [TestMethod]
    public void Close_WithHideToTrayBehavior_DeadTray_RunsTheNormalCloseSequence()
    {
        bool closedFired = false;
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        Host.Run<object?>(() =>
        {
            // The dead tray: a surface that can never bring the icon up
            // (the ico missing from the output, the 2026-08-25 trap). The
            // N1 guard falls the close through to the normal exit instead
            // of hiding into a void - a hidden window with no tray is
            // unreachable.
            var deadTray = new FakeTraySurface(showBringsUp: false);
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), deadTray, null, null, null, null, FakeTransport.InertEngine());
            window.Show();
            window.Closed += (_, _) => closedFired = true;
            try
            {
                window.Close();

                Assert.IsTrue(closedFired, "the N1 fallback runs the normal close (the teardown) when the tray is dead");
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
    public void ShowFromTray_AfterTheCloseIntercept_RestoresTheWindow_KeepsTheMaximizedState()
    {
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        Host.Run<object?>(() =>
        {
            var fake = new FakeTraySurface();
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, null, null, null, null, FakeTransport.InertEngine());
            try
            {
                window.Show();
                window.UpdateLayout();
                window.WindowState = WindowState.Maximized;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                window.Close(); // the intercept hides (Hide does not touch the state)
                Assert.IsFalse(window.IsVisible, "the close intercept hides the maximized window");

                window.ShowFromTray();

                Assert.IsTrue(window.IsVisible, "the tray show restores the hidden window");
                Assert.AreEqual(
                    WindowState.Maximized,
                    window.WindowState,
                    "the close-intercept leg preserves the window's own state - the user must not re-maximize every hide/restore cycle");
            }
            finally
            {
                window.QuitClose();
            }
            return null;
        });
    }

    [TestMethod]
    public void ShowFromTray_AfterTheMinimizeIntercept_RestoresInNormalState()
    {
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        Host.Run<object?>(() =>
        {
            var fake = new FakeTraySurface();
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, null, null, null, null, FakeTransport.InertEngine());
            try
            {
                window.Show();
                window.UpdateLayout();

                window.WindowState = WindowState.Minimized; // the intercept hides
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                Assert.IsFalse(window.IsVisible, "the minimize intercept hides the window");

                window.ShowFromTray();

                Assert.IsTrue(window.IsVisible, "the tray show restores the hidden window");
                Assert.AreEqual(
                    WindowState.Normal,
                    window.WindowState,
                    "the minimize leg leaves the window Minimized: the restore forces Normal back (a re-shown minimized window would be invisible)");
            }
            finally
            {
                window.QuitClose();
            }
            return null;
        });
    }

    [TestMethod]
    public void Minimize_WithADisabledOwner_VetoesTheHide()
    {
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        Host.Run<object?>(() =>
        {
            var fake = new FakeTraySurface();
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, null, null, null, null, FakeTransport.InertEngine());
            try
            {
                window.Show();
                window.UpdateLayout();
                // WPF disables the owner while a modal dialog is up
                // (ShowDialog): a system-wide minimize (Win+D) with the
                // dialog open must not hide the owner, or the hide
                // cascades to the dialog and the app disappears
                // mid-dialog.
                window.IsEnabled = false;

                window.WindowState = WindowState.Minimized;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                Assert.IsTrue(window.IsVisible, "the disabled owner vetoes the minimize hide (a modal dialog is open)");
                Assert.AreEqual(WindowState.Minimized, window.WindowState, "the minimize proceeds normally instead of hiding");
            }
            finally
            {
                window.IsEnabled = true;
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
