using System.ComponentModel;
using System.Windows;

namespace ModernWigiDash.App;

/// <summary>
/// The window's lifecycle module (App, ADR-0018/0019): the one owner of the
/// hide-to-tray intercepts, the restore-from-tray state rule, the explicit-quit
/// latch, and the session-end standby. The close and minimize intercepts route
/// through <see cref="CloseInterceptPolicy"/> (a hand-edited profile can never
/// smuggle in a hide; a dead tray falls through to the normal behavior), so the
/// two can never drift. The quit latch (<see cref="_quitting"/>) is set by the
/// tray's Quit before the close so the close intercept vetoes itself and the
/// tray's Quit always exits. The window keeps only the WPF event handlers that
/// forward into this module; the policy lives here.
/// </summary>
internal sealed class WindowLifecycle(
    Func<bool> wiredProvider,
    Func<string?> closeBehaviorProvider,
    Func<bool> trayLiveProvider,
    Func<bool> isEnabledProvider,
    Func<bool> isVisibleProvider,
    Func<WindowState> windowStateProvider,
    Action hide,
    Action show,
    Action activate,
    Action forceNormal,
    Action close,
    Action shutdown,
    Func<bool> runSessionEndStandby)
{
    private bool _quitting;
    private bool _startupMinimizeLatch;

    /// <summary>The explicit-quit flag (ADR-0018): set by the tray's Quit
    /// (<see cref="QuitClose"/>) before the close, so the close intercept - which
    /// hides to the tray when the close behavior is on - vetoes itself and the
    /// tray's Quit always exits.</summary>
    public void QuitClose()
    {
        _quitting = true;
        close();
    }

    /// <summary>The tray menu's "Quit" (ADR-0018): the explicit-quit path through
    /// the normal close sequence (OnClosing, the teardown plan, the display
    /// standby), then an explicit Shutdown. A hidden window's Close does not trip
    /// OnLastWindowClose, so without the Shutdown the process would linger with
    /// its icon already gone; WPF's Shutdown is idempotent, so the visible-window
    /// case stays a single shutdown.</summary>
    public void QuitFromTray()
    {
        QuitClose();
        shutdown();
    }

    /// <summary>The tray/second-launch activation: shows a hidden window
    /// (restoring it from the tray hide) and brings it forward. Both callers
    /// arrive on the UI thread (the tray icon's events are WPF-routed; the
    /// single-instance guard hops through the App's dispatcher).</summary>
    public void ShowFromTray()
    {
        if (!isVisibleProvider())
        {
            // The minimize-intercept leg leaves the window Minimized: force
            // Normal so the restore does not re-show minimized. The
            // close-intercept leg preserves the window's own state (a maximized
            // window comes back maximized), so only the Minimized state needs
            // the repair.
            if (windowStateProvider() == WindowState.Minimized)
            {
                forceNormal();
            }
            show();
        }
        activate();
    }

    /// <summary>The close intercept (ADR-0018): a window close (X, Alt+F4) hides
    /// to the tray instead of closing when the resolved close behavior is the
    /// tray keep-alive and the tray icon is live. With the behavior on and the
    /// tray dead (N1) the close falls through to the normal exit: a hidden
    /// window with no tray is unreachable, and losing the app is worse than
    /// leaving it.</summary>
    public void OnWindowClosing(CancelEventArgs e)
    {
        if (!wiredProvider() || _quitting)
        {
            return;
        }
        if (CloseInterceptPolicy.ShouldHide(closeBehaviorProvider(), trayLiveProvider()))
        {
            e.Cancel = true;
            hide();
        }
    }

    /// <summary>The minimize intercept (ADR-0018, M2): a minimize hides to the
    /// tray under the same policy as a close, so the window never lingers as a
    /// minimized taskbar entry the user would have to restore. Two vetoes keep
    /// the hide from swallowing the app: a disabled owner is behind a modal
    /// dialog (WPF disables the owner for ShowDialog), and a system-wide
    /// minimize (Win+D) with the dialog open would hide the owner and cascade the
    /// hide to the dialog, so the app disappears mid-dialog; the _quitting mirror
    /// of the close intercept keeps a state change mid-quit from hiding.</summary>
    public void OnWindowStateChanged()
    {
        // The autostart minimize (ADR-0019) is deliberate, not a user minimize:
        // the one-shot latch vetoes the hide for the startup state change only,
        // and it is cleared here before the other guards (the explicit clear in
        // the ctor covers the no-event path), so the first real user minimize
        // still intercepts.
        if (_startupMinimizeLatch)
        {
            _startupMinimizeLatch = false;
            return;
        }
        if (!wiredProvider() || _quitting || !isEnabledProvider() || windowStateProvider() != WindowState.Minimized)
        {
            return;
        }
        if (CloseInterceptPolicy.ShouldHide(closeBehaviorProvider(), trayLiveProvider()))
        {
            hide();
        }
    }

    /// <summary>Arms the one-shot autostart-minimize latch (ADR-0019) around the
    /// startup WindowState write so the minimize-to-tray intercept cannot hide
    /// the window the autostart path deliberately opened minimized. The startup
    /// state change's own event consumes it; the explicit clear after the write
    /// keeps it one-shot if no event fires for the change.</summary>
    public void ArmStartupMinimizeLatch() => _startupMinimizeLatch = true;

    /// <summary>Clears the one-shot autostart-minimize latch (the no-event path
    /// after the startup WindowState write).</summary>
    public void ClearStartupMinimizeLatch() => _startupMinimizeLatch = false;

    /// <summary>The session-end standby (ADR-0018): the production caller is the
    /// App's SessionEnding event. A system shutdown or logoff kills the process
    /// mid-frame-stream, and this is the one chance to run the display's standby
    /// ritual before the process dies. Returns the truthful verdict.</summary>
    public bool RunSessionEndStandby() => runSessionEndStandby();
}
