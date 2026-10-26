namespace ModernWigiDash.App;

/// <summary>
/// The system-session facet of <see cref="WindowLifecycle"/>: the
/// session-end standby probe (the display's standby ritual before a system
/// shutdown or logoff kills the process mid-frame-stream). Named as its
/// own facet because it is the module's one non-window concern.
/// </summary>
internal sealed record WindowLifecycleHooks(
    Func<bool> RunSessionEndStandby);
