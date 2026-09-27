using System.IO;
using System.Windows;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

/// <summary>
/// The WINDOW's half of the close intercept (ADR-0018), on a real STA window:
/// a real <see cref="Window.Close"/> must reach the lifecycle module, which
/// cancels the close and hides to the tray. This is the only claim here that
/// needs a window — it is the wiring from the WPF <c>Closing</c> event through
/// <c>OnWindowClosing</c> into <c>WindowLifecycle</c>, and a module test cannot
/// make that hop happen.
///
/// The module's own rules — the close and minimize intercepts, the quit latch, the
/// dead-tray fallback, the disabled-owner veto, the startup-minimize latch, the
/// restore-from-tray state repair, the tray Quit's shutdown and the session-end
/// standby — moved to <see cref="WindowLifecycleTests"/>, which drives the module
/// directly. They used to run through nine live windows here, each paying for an
/// XAML tree, a profile load, a telemetry session, a tray icon, a hotkey
/// registration, a temp settings store, a temp profile, a swapped FileLog path
/// and an STA thread, to assert a module whose interface is twelve delegates
/// (the architecture review's "wire nothing" seam candidate, 2026-09-27).
///
/// The tray surface is the injected live fake: the production surface reads dead
/// in the test host (its icon resource is not in the test output dir), and the
/// dead-tray fallback would swallow every hide.
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
    public void Close_WithHideToTrayBehavior_ReachesTheModuleAndHidesInsteadOfClosing()
    {
        bool closedFired = false;
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        Host.Run<object?>(() =>
        {
            var fake = new FakeTraySurface();
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, UsbEngine: FakeTransport.InertEngine()));
            try
            {
                window.Show();
                window.Closed += (_, _) => closedFired = true;

                window.Close();

                Assert.IsFalse(closedFired, "the WPF Closing event reached the module, which cancelled the close - the teardown must not run for a hidden window");
                Assert.IsFalse(window.IsVisible, "the window hides to the tray instead of closing");
                Assert.IsTrue(fake.IsLive, "the hide decision read the injected live tray surface (the production one reads dead here)");

                // The tray's Quit still exits: the quit latch makes the close
                // proceed through the normal sequence.
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
