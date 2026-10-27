using System;
using System.ComponentModel;
using System.Windows;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Deep shell lifecycle module: unifies window state intercepts, notification tray
/// icon lifecycle, session-end standby, and explicit-quit semantics into a single
/// coherent coordinator, eliminating the mutual trampoline between MainWindow,
/// WindowLifecycle, and TrayIconController (Candidate 1).
/// </summary>
internal sealed class AppShellLifecycle : IDisposable
{
    private readonly WindowLifecycle _lifecycle;
    private readonly TrayIconController _tray;

    /// <summary>
    /// Constructs a self-contained <see cref="AppShellLifecycle"/> bound to the given <see cref="Window"/>.
    /// </summary>
    public AppShellLifecycle(
        Window window,
        Func<bool> isWired,
        Func<string?> closeBehavior,
        Func<bool> runSessionEndStandby,
        ITrayIconSurface? traySurface = null,
        DiagLog? trayLog = null)
        : this(
            isWired: isWired,
            closeBehavior: closeBehavior,
            isOwnerEnabled: () => window.IsEnabled,
            isOwnerVisible: () => window.IsVisible,
            ownerState: () => window.WindowState,
            hideOwner: window.Hide,
            minimizeOwner: () => window.WindowState = WindowState.Minimized,
            showOwner: window.Show,
            activateOwner: () => { _ = window.Activate(); },
            forceNormalOwner: () => window.WindowState = WindowState.Normal,
            closeOwner: window.Close,
            shutdownApp: () => Application.Current?.Shutdown(),
            runSessionEndStandby: runSessionEndStandby,
            traySurface: traySurface,
            trayLog: trayLog)
    {
    }

    /// <summary>
    /// Pure in-memory constructor: allows testing the full shell lifecycle and tray interaction
    /// without a live WPF <see cref="Window"/> or UI thread dispatch races.
    /// </summary>
    public AppShellLifecycle(
        Func<bool> isWired,
        Func<string?> closeBehavior,
        Func<bool> isOwnerEnabled,
        Func<bool> isOwnerVisible,
        Func<WindowState> ownerState,
        Action hideOwner,
        Action minimizeOwner,
        Action showOwner,
        Action activateOwner,
        Action forceNormalOwner,
        Action closeOwner,
        Action shutdownApp,
        Func<bool> runSessionEndStandby,
        ITrayIconSurface? traySurface = null,
        DiagLog? trayLog = null)
    {
        _tray = new TrayIconController(
            onShow: () => _lifecycle!.ShowFromTray(),
            onQuit: () => _lifecycle!.QuitFromTray(),
            log: trayLog,
            surface: traySurface);

        _lifecycle = new WindowLifecycle(
            new WindowStateProbes(
                Wired: isWired,
                CloseBehavior: closeBehavior,
                TrayLive: () => _tray.IsLive,
                IsEnabled: isOwnerEnabled,
                IsVisible: isOwnerVisible,
                State: ownerState),
            new WindowSurface(
                Hide: hideOwner,
                Minimize: minimizeOwner,
                Show: showOwner,
                Activate: activateOwner,
                ForceNormal: forceNormalOwner,
                Close: closeOwner,
                Shutdown: shutdownApp),
            new WindowLifecycleHooks(
                RunSessionEndStandby: runSessionEndStandby));
    }

    /// <summary>Whether the tray icon is currently live in the notification area.</summary>
    public bool IsTrayLive => _tray.IsLive;

    /// <summary>The underlying tray icon controller.</summary>
    public TrayIconController Tray => _tray;

    /// <summary>The underlying window lifecycle manager.</summary>
    public WindowLifecycle Lifecycle => _lifecycle;

    /// <summary>Starts the notification tray icon surface.</summary>
    public void StartTray() => _tray.Start();

    /// <summary>Removes the notification tray icon and disposes its surface.</summary>
    public void DisposeTray() => _tray.Dispose();

    /// <summary>Shows and activates the window from the tray or secondary launch signal.</summary>
    public void ShowFromTray() => _lifecycle.ShowFromTray();

    /// <summary>Initiates explicit application shutdown via the tray menu.</summary>
    public void QuitFromTray() => _lifecycle.QuitFromTray();

    /// <summary>Closes the window directly with the explicit-quit latch armed.</summary>
    public void QuitClose() => _lifecycle.QuitClose();

    /// <summary>Executes the display standby ritual on OS session ending.</summary>
    public bool RunSessionEndStandby() => _lifecycle.RunSessionEndStandby();

    /// <summary>Handles window closing event with hide-to-tray intercept.</summary>
    public void OnWindowClosing(CancelEventArgs e) => _lifecycle.OnWindowClosing(e);

    /// <summary>Handles window state changes with minimize-to-tray intercept.</summary>
    public void OnWindowStateChanged() => _lifecycle.OnWindowStateChanged();

    /// <summary>Executes the startup window state ritual (startup minimized vs. hide to tray).</summary>
    public void RunStartupState(bool startMinimized, bool minimizeToTray, Action<Action> subscribeLoaded)
        => _lifecycle.RunStartupState(startMinimized, minimizeToTray, subscribeLoaded);

    /// <summary>Disposes shell lifecycle resources.</summary>
    public void Dispose()
    {
        _tray.Dispose();
    }
}
