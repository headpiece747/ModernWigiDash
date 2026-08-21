using Microsoft.Win32;

namespace ModernWigiDash.App.Power;

/// <summary>
/// The production <see cref="IPowerModeSource"/>: forwards only the
/// Suspend/Resume transitions from <see cref="SystemEvents.PowerModeChanged"/>
/// (StatusChange and unknown modes are never raised — the lifecycle policy
/// reacts only to sleep/wake).
/// </summary>
internal sealed class SystemPowerModeSource : IPowerModeSource
{
    public event Action<PowerModes>? ModeChanged;

    public SystemPowerModeSource()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? _, PowerModeChangedEventArgs e)
    {
        if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
        {
            ModeChanged?.Invoke(e.Mode);
        }
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
