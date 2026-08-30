using System.IO;
using System.Windows;
using System.Windows.Threading;
using ModernWigiDash.App.Power;
using AppClass = ModernWigiDash.App.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// The autostart-minimized launch pinned on a live STA window (ADR-0019): a
/// --startup launch with the tray keep-alive behavior opens the window
/// minimized (visible, never hidden - the one-shot latch vetoes the
/// minimize-to-tray intercept for the startup state change only), and the
/// first real user minimize after the startup still hides. The flag is the
/// static <c>App.StartMinimized</c> the App's OnStartup sets from the launch
/// args; the test sets it directly around the window's construction (the
/// test host's launch args never carry the flag).
/// </summary>
[TestClass]
public class WindowAutostartTests
{
    private static readonly StaHost Host = new("WindowAutostart-STA");

    [TestCleanup]
    public void Cleanup()
    {
        AppClass.StartMinimized = false;
        Host.DetachApplication();
    }

    [TestMethod]
    public void StartupMinimized_WithHideToTrayBehavior_OpensMinimizedWithoutHiding_AndTheNextMinimizeStillHides()
    {
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        Host.Run<object?>(() =>
        {
            AppClass.StartMinimized = true;
            var fake = new FakeTraySurface();
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, null, null, null, null, FakeTransport.InertEngine());
            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                Assert.IsTrue(window.IsVisible, "the autostart window is visible, not hidden - without the latch the hideToTray intercept would have hidden it at sign-in");
                Assert.AreEqual(WindowState.Minimized, window.WindowState, "the --startup flag opens the window minimized");

                // The latch is one-shot: the first real user minimize after
                // the startup still hides to the tray. Restore first (the
                // window is minimized), then minimize like a user would.
                window.WindowState = WindowState.Normal;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                window.WindowState = WindowState.Minimized;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                Assert.IsFalse(window.IsVisible, "the first real minimize after the startup still hides to the tray (the one-shot latch is spent)");
            }
            finally
            {
                window.QuitClose();
            }
            return null;
        });
    }

    [TestMethod]
    public void NormalLaunch_WithoutTheFlag_OpensAtTheNormalState()
    {
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        Host.Run<object?>(() =>
        {
            AppClass.StartMinimized = false;
            var fake = new FakeTraySurface();
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, null, null, null, null, FakeTransport.InertEngine());
            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                Assert.AreEqual(WindowState.Normal, window.WindowState, "without the flag the window opens at its normal state");
                Assert.IsTrue(window.IsVisible);
            }
            finally
            {
                window.QuitClose();
            }
            return null;
        });
    }

    /// <summary>Seeds a one-page profile with the given raw close-behavior
    /// value so the window's profile load picks it up the production way
    /// (the WindowCloseInterceptTests pattern).</summary>
    private static string SeedProfile(string? closeBehavior)
    {
        string dir = Path.Combine(Path.GetTempPath(), "wmd-autostart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string profilePath = Path.Combine(dir, "profile.json");
        string field = closeBehavior is null ? "" : $",\"CloseBehavior\":\"{closeBehavior}\"";
        File.WriteAllText(profilePath, $"{{\"ProfileId\":\"test\",\"Pages\":[{{\"PageName\":\"A\"}}]{field}}}");
        return profilePath;
    }
}
