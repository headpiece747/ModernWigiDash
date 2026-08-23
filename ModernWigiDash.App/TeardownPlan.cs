namespace ModernWigiDash.App;

/// <summary>
/// The window's teardown plan (App): the ordered teardown steps plus the
/// never-skip last resort (the display-standby guarantee), as named items —
/// one named artifact the close handler runs through
/// <see cref="ShutdownOrchestrator"/>. The sequence is assertable against the
/// real list without running teardown (the orchestrator's synthetic steps pin
/// the run policy, not the sequence).
/// </summary>
internal sealed record TeardownPlan(IReadOnlyList<TeardownStep> OrderedSteps, TeardownStep LastResort);

/// <summary>
/// One named teardown step: <see cref="Name"/> is the ordering fact the plan
/// pins (persist first, pump before delivery, engine last), <see cref="Run"/>
/// is the action.
/// </summary>
internal sealed record TeardownStep(string Name, Action Run);
