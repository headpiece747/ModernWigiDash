namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Pomodoro focus timer state.
/// </summary>
public readonly record struct FocusState(
    bool IsRunning,
    TimeSpan Elapsed,
    TimeSpan Duration,
    string? ActiveTaskId = null);
