using System.Runtime.InteropServices;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Hotkey;

internal delegate bool RegisterHotKeyFn(IntPtr hWnd, int id, int mod, ushort vk);
internal delegate void UnregisterHotKeyFn(IntPtr hWnd, int id, ushort vk);

/// <summary>
/// The RegisterHotKey/UnregisterHotKey P/Invoke surface as an injectable
/// delegate bag (the WinUsbApi house pattern): production binds the real
/// externs once via <see cref="Default"/>; tests inject managed fakes, so the
/// registration diff, the foreign-owned refusal, and the teardown unregister
/// are scriptable without an OS-level hotkey collision. Both calls acquire
/// and own no handle: the registration lives in the OS message loop and is
/// released by UnregisterHotKey (the manager's Dispose) or by the window's
/// own destruction.
/// </summary>
internal sealed class HotkeyApi(RegisterHotKeyFn registerHotKey, UnregisterHotKeyFn unregisterHotKey)
{
    /// <summary>The production binding: the real P/Invoke externs.</summary>
    public static readonly HotkeyApi Default = new(RegisterHotKeyPInvoke, UnregisterHotKeyPInvoke);

    internal RegisterHotKeyFn RegisterHotKey { get; } = registerHotKey;
    internal UnregisterHotKeyFn UnregisterHotKey { get; } = unregisterHotKey;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKeyPInvoke(IntPtr hWnd, int id, int fsModifiers, ushort vk);

    [DllImport("user32.dll")]
    private static extern void UnregisterHotKeyPInvoke(IntPtr hWnd, int id, ushort vk);
}
