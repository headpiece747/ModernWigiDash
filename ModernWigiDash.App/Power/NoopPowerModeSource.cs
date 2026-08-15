using Microsoft.Win32;

namespace ModernWigiDash.App.Power;

/// <summary>
/// The inert <see cref="IPowerModeSource"/> for test hosts: constructing a
/// MainWindow in a test must never subscribe to the real SystemEvents
/// (a hidden message window and a cross-thread event source in a test run).
/// </summary>
public sealed class NoopPowerModeSource : IPowerModeSource
{
    public event Action<PowerModes>? ModeChanged
    {
        add
        {
            // Intentional no-op: a test host must never subscribe to the real
            // SystemEvents (a hidden message window in a test run).
        }
        remove
        {
            // Intentional no-op: nothing was ever subscribed.
        }
    }

    public void Dispose()
    {
        // Intentional no-op: nothing was acquired.
    }
}
