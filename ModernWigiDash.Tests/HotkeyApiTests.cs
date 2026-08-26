using ModernWigiDash.App.Hotkey;

namespace ModernWigiDash.Tests;

/// <summary>
/// The production <see cref="HotkeyApi"/> binding pinned against the REAL
/// user32.dll: the P/Invoke externs must bind. The pin invokes the
/// production delegates with a degenerate call (HWND 0, id 0, no
/// modifiers, no key) - no usable global hotkey is created, nothing
/// collides, and no UIPI is involved - and immediately releases it. The
/// pin asserts the BOUNDING, not the verdict: a wrong entry-point name
/// throws EntryPointNotFoundException on exactly this first call (the
/// 2026-08-26 crash, where the externs bound the non-existent export
/// "RegisterHotKeyPInvoke" and the window died at SourceInitialized), and
/// that exception is invisible to every other hotkey test because they
/// all drive the fake api. The return value is deliberately not asserted:
/// an OS's verdict on a degenerate call is not contractually guaranteed
/// (it returns true on this machine), so only the successful binding is
/// load-bearing. The pin cannot use a GetProcAddress name lookup instead:
/// on this machine the P/Invoke binding of GetProcAddress itself (both the
/// A and W variants, PS 5.1 Add-Type and .NET 10) fails to resolve while
/// every other kernel32/user32 binding works, so the direct production
/// invocation is the only reliable binding proof here. The externs acquire
/// and own no handle: a live registration would be released by
/// UnregisterHotKey or the window's destruction, and a degenerate chord
/// never registers anything usable.
/// </summary>
[TestClass]
public class HotkeyApiTests
{
    private static readonly IntPtr InvalidHwnd = IntPtr.Zero;

    [TestMethod]
    public void Default_RegisterHotKey_BindsInTheRealUser32()
    {
        // The pin: the call must bind and execute. A wrong entry point
        // throws EntryPointNotFoundException before the OS ever sees the
        // call; the degenerate chord creates no usable registration and is
        // released immediately, so no OS state lingers.
        bool executed = false;
        try
        {
            HotkeyApi.Default.RegisterHotKey(InvalidHwnd, 0, 0, 0);
            executed = true;
        }
        catch (Exception ex)
        {
            Assert.Fail("RegisterHotKey threw instead of binding to the real user32 entry point: " + ex.Message);
        }

        Assert.IsTrue(executed, "the production RegisterHotKey binding must run against the real user32.dll");
        HotkeyApi.Default.UnregisterHotKey(InvalidHwnd, 0, 0);
    }

    [TestMethod]
    public void Default_UnregisterHotKey_BindsInTheRealUser32()
    {
        // A wrong entry point throws EntryPointNotFoundException; a bound
        // entry point is a no-op on a never-registered chord.
        bool executed = false;
        try
        {
            HotkeyApi.Default.UnregisterHotKey(InvalidHwnd, 0, 0);
            executed = true;
        }
        catch (Exception ex)
        {
            Assert.Fail("UnregisterHotKey threw instead of binding to the real user32 entry point: " + ex.Message);
        }

        Assert.IsTrue(executed, "the production UnregisterHotKey binding must run against the real user32.dll");
    }
}
