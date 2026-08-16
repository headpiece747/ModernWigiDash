using Microsoft.Win32;

namespace ModernWigiDash.App.Power;

/// <summary>
/// The power-state lifecycle policy: Suspend pauses the caller's work,
/// Resume restarts it. The window wires the actions (frame pump stop/start,
/// engine reconnect); this module owns only the mode→action mapping and the
/// subscription lifecycle — testable without windowing or SystemEvents.
/// </summary>
internal sealed class PowerLifecycle : IDisposable
{
    private readonly IPowerModeSource _source;
    private readonly Action _onSuspend;
    private readonly Action _onResume;

    public PowerLifecycle(IPowerModeSource source, Action onSuspend, Action onResume)
    {
        _source = source;
        _onSuspend = onSuspend;
        _onResume = onResume;
        _source.ModeChanged += OnModeChanged;
    }

    private void OnModeChanged(PowerModes mode)
    {
        if (mode == PowerModes.Suspend)
        {
            _onSuspend();
        }
        else if (mode == PowerModes.Resume)
        {
            _onResume();
        }
    }

    public void Dispose()
    {
        _source.ModeChanged -= OnModeChanged;
        _source.Dispose();
    }
}
