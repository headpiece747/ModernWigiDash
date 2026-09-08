namespace ModernWigiDash.App;

/// <summary>
/// The one-shot startup-minimize latch policy (ADR-0019): the autostart path
/// opens the window minimized, and the minimize-to-tray intercept must not hide
/// it at sign-in. The latch is armed around the startup WindowState write,
/// consumed by the first state-change event (or cleared explicitly if no event
/// fires), so the first real user minimize still intercepts. Pure decision over
/// the latch's current state, so the one-shot semantics are assertable without
/// a window or WPF events. WindowLifecycle binds the production latch field and
/// routes every arm/clear/consume through this module.
/// </summary>
internal static class StartupMinimizeLatchPolicy
{
    /// <summary>The latch state: whether the next state-change event should be
    /// vetoed (the startup minimize) or allowed to proceed (a real user
    /// minimize).</summary>
    public readonly record struct LatchState(bool Armed);

    /// <summary>Arms the latch for the next state-change event.</summary>
    public static LatchState Arm(LatchState state) => new(true);

    /// <summary>Clears the latch (the no-event path after the startup write).</summary>
    public static LatchState Clear(LatchState state) => new(false);

    /// <summary>Consumes the latch on a state-change event: returns true when
    /// the event should be vetoed (the startup minimize), and clears the latch
    /// in either case (one-shot).</summary>
    public static (bool Veto, LatchState Next) Consume(LatchState state)
        => (state.Armed, new LatchState(false));
}
