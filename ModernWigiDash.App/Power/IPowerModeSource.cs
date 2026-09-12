using Microsoft.Win32;

namespace ModernWigiDash.App.Power;

/// <summary>
/// The Windows power-mode seam: a source of Suspend/Resume notifications,
/// drivable by an in-memory fake in tests (the SystemEvents precedent). The
/// real adapter (<see cref="SystemPowerModeSource"/>) subscribes to
/// <see cref="Microsoft.Win32.SystemEvents.PowerModeChanged"/>; other power
/// modes (e.g. StatusChange) are filtered out at the adapter.
/// </summary>
internal interface IPowerModeSource : IDisposable
{
    /// <summary>Raised on the system's power event thread: Suspend when Windows
    /// enters sleep, Resume when it wakes. Subscribe before use; raise from
    /// any thread.</summary>
    event Action<PowerModes>? ModeChanged;
}
