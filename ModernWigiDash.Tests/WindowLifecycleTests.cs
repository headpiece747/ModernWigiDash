using System.ComponentModel;
using System.Windows;
using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window lifecycle module's own test surface (ADR-0018/0019): the
/// hide-to-tray close and minimize intercepts, the restore-from-tray state rule,
/// the explicit-quit latch, the disabled-owner veto, the startup-minimize latch,
/// the tray Quit's explicit shutdown, and the session-end standby.
///
/// None of these had a test class. All nine cases ran through a live
/// MainWindow — an XAML tree, a profile load, a telemetry session, a tray icon, a
/// hotkey registration, a temp settings store, a temp profile, a swapped FileLog
/// path and an STA thread each — to assert that a module whose entire interface
/// is twelve <c>Func</c>/<c>Action</c> parameters did the right thing
/// (the architecture review's "wire nothing" seam candidate, 2026-09-27).
///
/// <see cref="LifecycleHarness"/> is the WPF contract, stated once: Hide() clears
/// IsVisible, Show() sets it, forceNormal sets the state, Activate changes
/// nothing. A reader comparing this to the window test it replaces should find
/// each assertion here and see which WPF fact it is standing in for.
/// </summary>
[TestClass]
public class WindowLifecycleTests
{
    /// <summary>The WPF facts the module's delegates stand in for, modelled once
    /// so each test states only the app's own rule.</summary>
    private sealed class LifecycleHarness
    {
        public bool Wired { get; set; } = true;
        public string? CloseBehavior { get; set; } = CloseBehaviorPolicy.HideToTray;
        public bool TrayLive { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public bool Visible { get; set; } = true;
        public WindowState State { get; set; } = WindowState.Normal;
        public bool StandbyVerdict { get; set; } = true;

        public int HideCalls { get; private set; }
        public int MinimizeCalls { get; private set; }
        public int ShowCalls { get; private set; }
        public int ActivateCalls { get; private set; }
        public int ForceNormalCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public int ShutdownCalls { get; private set; }
        public int StandbyCalls { get; private set; }

        public List<Action> LoadedHandlers { get; } = [];

        public WindowLifecycle Build() => new(
            new WindowStateProbes(
                Wired: () => Wired,
                CloseBehavior: () => CloseBehavior,
                TrayLive: () => TrayLive,
                IsEnabled: () => Enabled,
                IsVisible: () => Visible,
                State: () => State),
            // WPF: Hide() clears IsVisible; the module's hide is the tray hide.
            new WindowSurface(
                Hide: () => { HideCalls++; Visible = false; },
                Minimize: () => { MinimizeCalls++; State = WindowState.Minimized; },
                Show: () => { ShowCalls++; Visible = true; },
                Activate: () => ActivateCalls++,
                ForceNormal: () => { ForceNormalCalls++; State = WindowState.Normal; },
                Close: () => CloseCalls++,
                Shutdown: () => ShutdownCalls++),
            new WindowLifecycleHooks(
                RunSessionEndStandby: () => { StandbyCalls++; return StandbyVerdict; }));

        /// <summary>Stands in for the window's <c>Loaded</c> event (the handler the minimize-to-tray startup path subscribes).</summary>
        public void RaiseLoaded()
        {
            foreach (Action handler in LoadedHandlers)
                handler();
        }
    }

    private static CancelEventArgs ClosingArgs() => new();

    [TestMethod]
    public void OnWindowClosing_HideToTrayAndLiveTray_CancelsAndHides()
    {
        var h = new LifecycleHarness();
        var lifecycle = h.Build();
        var e = ClosingArgs();

        lifecycle.OnWindowClosing(e);

        Assert.IsTrue(e.Cancel, "the intercept cancels the close - the teardown must not run for a hidden window");
        Assert.AreEqual(1, h.HideCalls, "the window hides to the tray instead of closing");
    }

    [TestMethod]
    public void OnWindowClosing_QuitBehavior_RunsTheNormalSequence()
    {
        var h = new LifecycleHarness { CloseBehavior = CloseBehaviorPolicy.Quit };
        var lifecycle = h.Build();
        var e = ClosingArgs();

        lifecycle.OnWindowClosing(e);

        Assert.IsFalse(e.Cancel, "with the pre-feature behavior the close runs the normal sequence (the teardown)");
        Assert.AreEqual(0, h.HideCalls, "a quit-behavior close never hides to the tray");
    }

    [TestMethod]
    public void OnWindowClosing_DeadTray_FallsThroughToTheNormalSequence()
    {
        // The N1 case: a hidden window with no tray icon is unreachable, so
        // losing the app is worse than leaving it minimized in the taskbar.
        var h = new LifecycleHarness { TrayLive = false };
        var lifecycle = h.Build();
        var e = ClosingArgs();

        lifecycle.OnWindowClosing(e);

        Assert.IsFalse(e.Cancel, "a dead tray must not strand a hidden window - the close falls through");
        Assert.AreEqual(0, h.HideCalls, "nothing to hide to");
    }

    [TestMethod]
    public void OnWindowClosing_BeforeWired_DoesNotCancel()
    {
        // The intercept is armed only after every startup module exists; a close
        // during construction must not hide a window the user cannot restore.
        var h = new LifecycleHarness { Wired = false };
        var lifecycle = h.Build();
        var e = ClosingArgs();

        lifecycle.OnWindowClosing(e);

        Assert.IsFalse(e.Cancel, "an unwired window's close is not intercepted");
        Assert.AreEqual(0, h.HideCalls);
    }

    [TestMethod]
    public void QuitClose_LatchesSoTheCloseInterceptVetoesItself()
    {
        var h = new LifecycleHarness();
        var lifecycle = h.Build();

        lifecycle.QuitClose();
        var e = ClosingArgs();
        lifecycle.OnWindowClosing(e);

        Assert.AreEqual(1, h.CloseCalls, "the tray's Quit runs the close");
        Assert.IsFalse(e.Cancel, "the explicit-quit latch bypasses the intercept, so the tray's Quit always exits");
    }

    [TestMethod]
    public void QuitFromTray_ClosesThenShutsDown()
    {
        // A hidden window's Close does not trip OnLastWindowClose, so without the
        // explicit Shutdown the process would linger with its icon already gone.
        var h = new LifecycleHarness();
        var lifecycle = h.Build();

        lifecycle.QuitFromTray();

        Assert.AreEqual(1, h.CloseCalls, "the quit runs the normal close sequence");
        Assert.AreEqual(1, h.ShutdownCalls, "and then an explicit shutdown");
    }

    [TestMethod]
    public void OnWindowStateChanged_MinimizedUnderHideToTray_Hides()
    {
        var h = new LifecycleHarness { State = WindowState.Minimized };
        var lifecycle = h.Build();

        lifecycle.OnWindowStateChanged();

        Assert.AreEqual(1, h.HideCalls, "the minimize hides to the tray instead of leaving a minimized taskbar entry");
    }

    [TestMethod]
    public void OnWindowStateChanged_DisabledOwner_VetoesTheHide()
    {
        // WPF disables the owner for ShowDialog, so a Win+D minimize with a modal
        // open would hide the owner and cascade the hide to the dialog - the app
        // would disappear mid-dialog.
        var h = new LifecycleHarness { State = WindowState.Minimized, Enabled = false };
        var lifecycle = h.Build();

        lifecycle.OnWindowStateChanged();

        Assert.AreEqual(0, h.HideCalls, "a disabled owner vetoes the minimize hide (a modal dialog is open)");
        Assert.AreEqual(WindowState.Minimized, h.State, "the minimize proceeds normally instead of hiding");
    }

    [TestMethod]
    public void OnWindowStateChanged_StartupLatchVetoesTheFirstMinimize_ThenConsumesIt()
    {
        // The autostart minimize is deliberate, not a user minimize: the one-shot
        // latch covers the startup state change only, and it is consumed before
        // the other guards so the first REAL user minimize still intercepts.
        var h = new LifecycleHarness { State = WindowState.Minimized };
        var lifecycle = h.Build();

        lifecycle.ArmStartupMinimizeLatch();
        lifecycle.OnWindowStateChanged();
        Assert.AreEqual(0, h.HideCalls, "the armed startup latch vetoes the autostart minimize's own state change");

        lifecycle.OnWindowStateChanged();
        Assert.AreEqual(1, h.HideCalls, "the latch is one-shot: the next real minimize intercepts");
    }

    [TestMethod]
    public void OnWindowStateChanged_StartupLatchCleared_IsInertSoARealMinimizeStillHides()
    {
        // WPF may raise no state change for the startup write, so the ctor clears
        // the latch explicitly on that no-event path. A CLEAR is not a veto: it
        // retires the one-shot arm, so the next real user minimize must still
        // intercept. (The first draft of this test asserted the opposite - that a
        // cleared latch still vetoes - and the gate caught it. The over-veto would
        // have been a real bug: the first user minimize after a sign-in would have
        // left a minimized taskbar entry with no way to hide it.)
        var h = new LifecycleHarness { State = WindowState.Minimized };
        var lifecycle = h.Build();

        lifecycle.ArmStartupMinimizeLatch();
        lifecycle.ClearStartupMinimizeLatch();
        lifecycle.OnWindowStateChanged();

        Assert.AreEqual(1, h.HideCalls, "a cleared latch leaves the real minimize path intact");
    }

    [TestMethod]
    public void OnWindowStateChanged_NotMinimized_DoesNothing()
    {
        var h = new LifecycleHarness { State = WindowState.Normal };
        var lifecycle = h.Build();

        lifecycle.OnWindowStateChanged();

        Assert.AreEqual(0, h.HideCalls, "only a minimize is intercepted");
    }

    [TestMethod]
    public void RunStartupState_AutostartMinimized_MinimizesNowAndClearsTheLatch()
    {
        var h = new LifecycleHarness();
        var lifecycle = h.Build();

        lifecycle.RunStartupState(startMinimized: true, minimizeToTray: false, subscribeLoaded: h.LoadedHandlers.Add);

        Assert.AreEqual(1, h.MinimizeCalls, "the autostart path minimizes the window at startup");
        Assert.AreEqual(WindowState.Minimized, h.State);
        Assert.AreEqual(0, h.LoadedHandlers.Count, "the autostart path defers nothing (the tray path is the one that subscribes Loaded)");
        lifecycle.OnWindowStateChanged();
        Assert.AreEqual(1, h.HideCalls, "the latch cleared with the write, so the first real minimize still intercepts");
    }

    [TestMethod]
    public void RunStartupState_MinimizeToTray_HidesFromTheLoadedHandlerUnderTheLatch()
    {
        var h = new LifecycleHarness { State = WindowState.Minimized };
        var lifecycle = h.Build();

        lifecycle.RunStartupState(startMinimized: false, minimizeToTray: true, subscribeLoaded: h.LoadedHandlers.Add);
        Assert.AreEqual(0, h.HideCalls, "the hide is deferred to Loaded (Hide must run after Show)");

        h.RaiseLoaded();
        Assert.AreEqual(1, h.HideCalls, "the Loaded handler hides the window behind the tray icon");
        lifecycle.OnWindowStateChanged();
        Assert.AreEqual(2, h.HideCalls, "the latch cleared with the hide, so a real minimize after startup still intercepts");
    }

    [TestMethod]
    public void RunStartupState_BothPaths_ComposeWithTheHideWinning()
    {
        var h = new LifecycleHarness();
        var lifecycle = h.Build();

        lifecycle.RunStartupState(startMinimized: true, minimizeToTray: true, subscribeLoaded: h.LoadedHandlers.Add);

        Assert.AreEqual(1, h.MinimizeCalls, "the autostart minimize runs at startup");
        h.RaiseLoaded();
        Assert.AreEqual(1, h.HideCalls, "and the tray flag's hide runs after Show (Hide wins over Minimized)");
    }

    [TestMethod]
    public void ShowFromTray_AfterTheCloseIntercept_RestoresAndKeepsTheMaximizedState()
    {
        // The close intercept preserves the window's own state, so a maximized
        // window comes back maximized.
        var h = new LifecycleHarness { State = WindowState.Maximized };
        var lifecycle = h.Build();
        lifecycle.OnWindowClosing(ClosingArgs());
        Assert.IsFalse(h.Visible, "the close intercept hid the maximized window");

        lifecycle.ShowFromTray();

        Assert.IsTrue(h.Visible, "the tray show restores the hidden window");
        Assert.AreEqual(0, h.ForceNormalCalls, "a maximized window is not forced to Normal - it comes back maximized");
        Assert.AreEqual(1, h.ActivateCalls, "and it is brought forward");
    }

    [TestMethod]
    public void ShowFromTray_AfterTheMinimizeIntercept_ForcesNormalState()
    {
        // The minimize-intercept leg leaves the window Minimized, so the restore
        // has to repair it or it re-shows minimized.
        var h = new LifecycleHarness { State = WindowState.Minimized };
        var lifecycle = h.Build();
        lifecycle.OnWindowStateChanged();
        Assert.IsFalse(h.Visible, "the minimize intercept hid the window");

        lifecycle.ShowFromTray();

        Assert.IsTrue(h.Visible, "the tray show restores the hidden window");
        Assert.AreEqual(1, h.ForceNormalCalls, "a minimized window is forced to Normal so the restore is not minimized again");
        Assert.AreEqual(WindowState.Normal, h.State);
    }

    [TestMethod]
    public void ShowFromTray_VisibleAndMinimized_RestoresToNormalWithoutShowing()
    {
        // A window the user minimized to the TASKBAR is still visible; the restore
        // must repair the state without a redundant Show.
        var h = new LifecycleHarness { Visible = true, State = WindowState.Minimized };
        var lifecycle = h.Build();

        lifecycle.ShowFromTray();

        Assert.AreEqual(0, h.ShowCalls, "an already-visible window is not shown again");
        Assert.AreEqual(1, h.ForceNormalCalls, "but a minimized visible window still needs the Normal repair");
        Assert.AreEqual(1, h.ActivateCalls, "Activate brings it to the foreground");
    }

    [TestMethod]
    public void RunSessionEndStandby_RoutesThroughTheSeamAndReturnsItsVerdict()
    {
        var h = new LifecycleHarness { StandbyVerdict = true };
        var lifecycle = h.Build();

        Assert.IsTrue(lifecycle.RunSessionEndStandby(), "the seam's verdict rides back through the module");
        Assert.AreEqual(1, h.StandbyCalls, "the routing calls the seam exactly once per session end");

        // The verdict is the display's, not the module's: a false must ride back
        // false rather than being swallowed into a "handled" claim.
        var failing = new LifecycleHarness { StandbyVerdict = false }.Build();
        Assert.IsFalse(failing.RunSessionEndStandby(), "a failed standby must report failure, not success");
    }
}
