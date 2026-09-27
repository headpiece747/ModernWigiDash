using System.IO;
using System.Windows;
using System.Windows.Threading;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

/// <summary>
/// The autostart-minimized launch pinned on a live STA window (ADR-0019): a
/// --startup launch with the tray keep-alive behavior opens the window
/// minimized (visible, never hidden - the one-shot latch vetoes the
/// minimize-to-tray intercept for the startup state change only), and the
/// first real user minimize after the startup still hides. The flag is the
/// static <c>ProcessLifecycle.StartMinimized</c> the App's OnStartup sets from the launch
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
        ProcessLifecycle.StartMinimized = false;
        Host.DetachApplication();
    }

    [TestMethod]
    public void StartupMinimized_OpensMinimizedWithoutHiding_AndTheNextMinimizeStillHides()
    {
        string profilePath = SeedProfile(CloseBehaviorPolicy.HideToTray);
        Host.Run<object?>(() =>
        {
            ProcessLifecycle.StartMinimized = true;
            var fake = new FakeTraySurface();
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), fake, UsbEngine: FakeTransport.InertEngine()));
            try
            {
                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                Assert.IsTrue(window.IsVisible, "the autostart window is visible, not hidden - without the latch the hideToTray intercept would have hidden it at sign-in");
                Assert.AreEqual(WindowState.Minimized, window.WindowState, "the --startup flag opens the window minimized");

                // What this proves, and what it does NOT - both checked by mutation,
                // because the first answer here was wrong twice:
                //
                // It proves the post-constructor state: the ctor's own
                // "WindowState = Minimized" write raises StateChanged
                // SYNCHRONOUSLY, which consumes the armed latch, so the user's
                // first real minimize is a normal intercept and hides. The
                // Normal round-trip is needed because the window is already
                // Minimized: setting Minimized again raises no change.
                //
                // It does NOT prove that the ctor calls ClearStartupMinimizeLatch.
                // Deleting that call leaves the suite green, because the ctor's own
                // state write has already consumed the latch - the explicit clear
                // is belt-and-braces for the no-event path its comment names, and
                // in this scenario it is redundant. I twice claimed this assertion
                // was covered elsewhere and removed it; the mutation showed the
                // claim was unproven, and then showed why it can never be pinned
                // here. The assertion stays because the post-constructor state is
                // the real user-visible claim; the defensive clear is a
                // production observation, not a test obligation.
                window.WindowState = WindowState.Normal;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                window.WindowState = WindowState.Minimized;
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                Assert.IsFalse(window.IsVisible, "the first real minimize after the startup still hides to the tray (the ctor's one-shot latch is spent)");
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
            ProcessLifecycle.StartMinimized = false;
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
                ProcessLifecycle.StartMinimized = false;
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
                ProcessLifecycle.StartMinimized = false;
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
                ProcessLifecycle.StartMinimized = false;
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
