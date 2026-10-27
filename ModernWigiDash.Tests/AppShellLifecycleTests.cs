using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// Unit tests for <see cref="AppShellLifecycle"/> (Candidate 1): asserts the deep
/// shell lifecycle module unifies window state intercepts, tray icon lifecycle,
/// explicit-quit semantics, and session-end standby without requiring a real WPF window.
/// </summary>
[TestClass]
public class AppShellLifecycleTests
{
    private sealed class ShellHarness
    {
        public bool Wired { get; set; } = true;
        public string? CloseBehavior { get; set; } = CloseBehaviorPolicy.HideToTray;
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

        public FakeTraySurface FakeTray { get; }

        public ShellHarness(bool trayBringsUp = true)
        {
            FakeTray = new FakeTraySurface(showBringsUp: trayBringsUp);
        }

        public AppShellLifecycle Build() => new(
            isWired: () => Wired,
            closeBehavior: () => CloseBehavior,
            isOwnerEnabled: () => Enabled,
            isOwnerVisible: () => Visible,
            ownerState: () => State,
            hideOwner: () => { HideCalls++; Visible = false; },
            minimizeOwner: () => { MinimizeCalls++; State = WindowState.Minimized; },
            showOwner: () => { ShowCalls++; Visible = true; },
            activateOwner: () => ActivateCalls++,
            forceNormalOwner: () => { ForceNormalCalls++; State = WindowState.Normal; },
            closeOwner: () => CloseCalls++,
            shutdownApp: () => ShutdownCalls++,
            runSessionEndStandby: () => { StandbyCalls++; return StandbyVerdict; },
            traySurface: FakeTray);
    }

    [TestMethod]
    public void StartTray_MakesTrayLive_AndDisposeTrayClearsLive()
    {
        var h = new ShellHarness();
        using var shell = h.Build();

        Assert.IsFalse(shell.IsTrayLive, "tray icon should be inert before StartTray");

        shell.StartTray();
        Assert.IsTrue(shell.IsTrayLive, "tray icon should be live after StartTray");

        shell.DisposeTray();
        Assert.IsFalse(shell.IsTrayLive, "tray icon should be dead after DisposeTray");
    }

    [TestMethod]
    public void TrayClick_ShowsAndActivatesWindow()
    {
        var h = new ShellHarness { Visible = false };
        using var shell = h.Build();
        shell.StartTray();

        h.FakeTray.RaiseSingleClick();

        Assert.AreEqual(1, h.ShowCalls, "tray single click must trigger window Show");
        Assert.AreEqual(1, h.ActivateCalls, "tray single click must trigger window Activate");
        Assert.IsTrue(h.Visible, "window visibility must be restored");
    }

    [TestMethod]
    public void TrayQuit_ArmsLatch_ClosesWindow_AndShutsDown()
    {
        var h = new ShellHarness();
        using var shell = h.Build();
        shell.StartTray();

        h.FakeTray.RaiseMenu(TrayMenuCommand.Quit);

        Assert.AreEqual(1, h.CloseCalls, "tray quit triggers window close");
        Assert.AreEqual(1, h.ShutdownCalls, "tray quit executes app shutdown");
    }

    [TestMethod]
    public void OnWindowClosing_WhenHideToTrayAndTrayLive_CancelsAndHides()
    {
        var h = new ShellHarness { CloseBehavior = CloseBehaviorPolicy.HideToTray };
        using var shell = h.Build();
        shell.StartTray();

        var cancelArgs = new CancelEventArgs();
        shell.OnWindowClosing(cancelArgs);

        Assert.IsTrue(cancelArgs.Cancel, "close event must be canceled when configured to hide to tray");
        Assert.AreEqual(1, h.HideCalls, "window should be hidden");
        Assert.IsFalse(h.Visible);
    }

    [TestMethod]
    public void OnWindowClosing_WhenCloseBehaviorQuit_AllowsClose()
    {
        var h = new ShellHarness { CloseBehavior = CloseBehaviorPolicy.Quit };
        using var shell = h.Build();
        shell.StartTray();

        var cancelArgs = new CancelEventArgs();
        shell.OnWindowClosing(cancelArgs);

        Assert.IsFalse(cancelArgs.Cancel, "close event must not be canceled when behavior is Quit");
        Assert.AreEqual(0, h.HideCalls);
    }

    [TestMethod]
    public void OnWindowClosing_WhenQuitCloseInvoked_BypassesHideIntercept()
    {
        var h = new ShellHarness { CloseBehavior = CloseBehaviorPolicy.HideToTray };
        using var shell = h.Build();
        shell.StartTray();

        shell.QuitClose();

        Assert.AreEqual(1, h.CloseCalls, "QuitClose invokes owner close");

        var cancelArgs = new CancelEventArgs();
        shell.OnWindowClosing(cancelArgs);
        Assert.IsFalse(cancelArgs.Cancel, "armed explicit-quit latch bypasses hide-to-tray intercept");
    }

    [TestMethod]
    public void OnWindowStateChanged_WhenMinimizedAndHideToTray_HidesWindow()
    {
        var h = new ShellHarness
        {
            CloseBehavior = CloseBehaviorPolicy.HideToTray,
            State = WindowState.Minimized
        };
        using var shell = h.Build();
        shell.StartTray();

        shell.OnWindowStateChanged();

        Assert.AreEqual(1, h.HideCalls, "minimizing should trigger hide when HideToTray is set");
        Assert.IsFalse(h.Visible);
    }

    [TestMethod]
    public void ShowFromTray_WhenMinimized_ForcesNormal()
    {
        var h = new ShellHarness
        {
            Visible = false,
            State = WindowState.Minimized
        };
        using var shell = h.Build();
        shell.StartTray();

        shell.ShowFromTray();

        Assert.AreEqual(1, h.ShowCalls);
        Assert.AreEqual(1, h.ForceNormalCalls, "restoring from minimized state must force Normal state");
        Assert.AreEqual(1, h.ActivateCalls);
    }

    [TestMethod]
    public void RunStartupState_StartMinimized_SetsMinimizedState()
    {
        var h = new ShellHarness();
        using var shell = h.Build();

        List<Action> loadedHandlers = [];
        shell.RunStartupState(startMinimized: true, minimizeToTray: false, subscribeLoaded: loadedHandlers.Add);

        Assert.AreEqual(1, h.MinimizeCalls, "startMinimized should minimize window immediately");
        Assert.AreEqual(0, loadedHandlers.Count, "no loaded handler needed when not minimizing to tray");
    }

    [TestMethod]
    public void RunStartupState_MinimizeToTray_HidesOnLoaded()
    {
        var h = new ShellHarness();
        using var shell = h.Build();

        List<Action> loadedHandlers = [];
        shell.RunStartupState(startMinimized: false, minimizeToTray: true, subscribeLoaded: loadedHandlers.Add);

        Assert.AreEqual(1, loadedHandlers.Count, "loaded handler registered for minimize-to-tray");
        loadedHandlers[0]();

        Assert.AreEqual(1, h.HideCalls, "loaded callback hides window to tray");
    }

    [TestMethod]
    public void RunSessionEndStandby_DelegatesToHookAndReturnsVerdict()
    {
        var h = new ShellHarness { StandbyVerdict = true };
        using var shell = h.Build();

        bool result = shell.RunSessionEndStandby();

        Assert.IsTrue(result);
        Assert.AreEqual(1, h.StandbyCalls);
    }
}
