using System.Windows;
using System.Windows.Interop;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Hotkey;

/// <summary>
/// The WPF↔Win32 attachment half of the global-hotkey module (ADR-0019):
/// the window handle the registrations ride on, the WM_HOTKEY WndProc hook,
/// and the refresh-pass trigger. Owns the registration/diff core (the
/// <see cref="GlobalHotkeyManager"/>), so "how do global hotkeys reach the
/// window" is one module: the window keeps only the seam (it hands the
/// module its HWND at SourceInitialized and calls one teardown step). The
/// message routing is testable without WPF (<see cref="OnHotkeyMessage"/>
/// over a fake <see cref="HotkeyApi"/>).
/// </summary>
internal sealed class GlobalHotkeyAttachment(HotkeyApi api, DiagLog log) : IDisposable
{
    private readonly GlobalHotkeyManager _manager = new(api, log);
    private IntPtr _hwnd = IntPtr.Zero;
    private HwndSource? _source;

    /// <summary>
    /// The global-hotkey attach: the window's HWND is created at Show (the
    /// wiring ran in the constructor, before the handle existed), so the
    /// attach adds the WM_HOTKEY hook to the window's HwndSource and runs
    /// the first idempotent registration pass. A hidden or minimized window
    /// keeps pumping its message loop, so the hotkeys work while the app
    /// is tray-hidden.
    /// </summary>
    public void Attach(Window window, Func<IReadOnlyList<DesiredGlobalHotkey>> candidates, Func<bool> killSwitch)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(OnWndProc);
        Refresh(candidates, killSwitch);
    }

    /// <summary>
    /// The global-hotkey registration trigger: hands the profile's provider
    /// widgets (the policy's profile-order walk) and the machine-local kill
    /// switch to the manager's pass, which resolves the desired set, logs
    /// the duplicate conflicts, and runs the register/unregister diff. Runs
    /// on the first Show (SourceInitialized), every profile mutation, a
    /// chord edit (the PersistProperty tail), a kill-switch toggle, and an
    /// interpreter-path change. A zero handle (pre-attach or post-teardown)
    /// is a no-op.
    /// </summary>
    public void Refresh(Func<IReadOnlyList<DesiredGlobalHotkey>> candidates, Func<bool> killSwitch)
    {
        if (_hwnd == IntPtr.Zero) return;
        _manager.RefreshPass(_hwnd, candidates(), killSwitch());
    }

    /// <summary>
    /// The WM_HOTKEY routing (0x0312): the message's wParam is the
    /// registered hotkey's id; the manager routes it to the owning widget's
    /// fire path (the same entry point as a tap). An unknown id leaves the
    /// message for the default handler. Test seam: the routing is drivable
    /// without a window.
    /// </summary>
    internal IntPtr OnHotkeyMessage(int msg, IntPtr wParam, ref bool handled)
    {
        if (msg == 0x0312 && _manager.Fire(wParam.ToInt32()))
            handled = true;
        return IntPtr.Zero;
    }

    /// <summary>Test seam: pre-registers a desired set on a handle without the WPF attach leg.</summary>
    internal void RegisterForTest(IntPtr handle, IReadOnlyList<DesiredGlobalHotkey> desired)
        => _manager.Refresh(handle, desired);

    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        => OnHotkeyMessage(msg, wParam, ref handled);

    /// <summary>Releases every registration on the last known handle and unhooks the WndProc (the window's teardown step).</summary>
    public void Dispose()
    {
        _manager.Dispose();
        _source?.RemoveHook(OnWndProc);
        _source = null;
        _hwnd = IntPtr.Zero;
    }
}
