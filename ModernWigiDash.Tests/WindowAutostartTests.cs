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
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, UsbEngine: FakeTransport.InertEngine()));
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
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, UsbEngine: FakeTransport.InertEngine()));
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

    [TestMethod]
    public void MinimizeToTrayOnStartup_WithLiveTray_OpensHidden()
    {
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        string settingsDir = Path.Combine(Path.GetTempPath(), "wmd-mtts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "app_settings.json");
        File.WriteAllText(settingsPath, "{\"MinimizeToTrayOnStartup\":true}");
        try
        {
            Host.Run<object?>(() =>
            {
                AppClass.StartMinimized = false;
                var fake = new FakeTraySurface();
                var store = new AppSettingsStore(settingsPath);
                var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, UsbEngine: FakeTransport.InertEngine(), AppSettingsStore: store));
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    // Pump the dispatcher so the Loaded handler (which calls Hide)
                    // runs: the StartupUri window is shown by WPF after the ctor,
                    // and the Loaded event fires on the first render pass.
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                    Assert.IsFalse(window.IsVisible, "the minimize-to-tray-on-startup flag opens the window hidden behind the tray icon");
                }
                finally
                {
                    window.QuitClose();
                }
                return null;
            });
        }
        finally
        {
            Directory.Delete(settingsDir, recursive: true);
        }
    }

    [TestMethod]
    public void MinimizeToTrayOnStartup_False_OpensNormally()
    {
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        string settingsDir = Path.Combine(Path.GetTempPath(), "wmd-mtts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "app_settings.json");
        File.WriteAllText(settingsPath, "{\"MinimizeToTrayOnStartup\":false}");
        try
        {
            Host.Run<object?>(() =>
            {
                AppClass.StartMinimized = false;
                var fake = new FakeTraySurface();
                var store = new AppSettingsStore(settingsPath);
                var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, UsbEngine: FakeTransport.InertEngine(), AppSettingsStore: store));
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                    Assert.IsTrue(window.IsVisible, "without the flag the window opens normally (visible)");
                    Assert.AreEqual(WindowState.Normal, window.WindowState);
                }
                finally
                {
                    window.QuitClose();
                }
                return null;
            });
        }
        finally
        {
            Directory.Delete(settingsDir, recursive: true);
        }
    }

    [TestMethod]
    public void MinimizeToTrayOnStartup_WithDeadTray_OpensNormally()
    {
        // The N1 guard: a hidden window with no live tray is unreachable, so
        // the flag must NOT hide when the tray is dead. The ctor skips the
        // Loaded handler entirely (the _tray.IsLive gate), so the window
        // opens normally regardless of the flag.
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        string settingsDir = Path.Combine(Path.GetTempPath(), "wmd-mtts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        string settingsPath = Path.Combine(settingsDir, "app_settings.json");
        File.WriteAllText(settingsPath, "{\"MinimizeToTrayOnStartup\":true}");
        try
        {
            Host.Run<object?>(() =>
            {
                AppClass.StartMinimized = false;
                // A dead tray surface: IsLive is false before Start.
                var deadTray = new FakeTraySurface(showBringsUp: false);
                var store = new AppSettingsStore(settingsPath);
                var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), deadTray, UsbEngine: FakeTransport.InertEngine(), AppSettingsStore: store));
                try
                {
                    window.Show();
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                    Assert.IsTrue(window.IsVisible, "with a dead tray the flag must not hide the window (the N1 guard: a hidden window with no tray is unreachable)");
                    Assert.AreEqual(WindowState.Normal, window.WindowState);
                }
                finally
                {
                    window.QuitClose();
                }
                return null;
            });
        }
        finally
        {
            Directory.Delete(settingsDir, recursive: true);
        }
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
