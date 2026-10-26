namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// State container persisted to disk for the companion task system.
/// </summary>
public sealed record TodoDataState
{
    /// <summary>Stored tasks list.</summary>
    public List<TodoItem> Tasks { get; init; } = [];

    /// <summary>Companion pet gamification state.</summary>
    public CozyCompanionState Companion { get; init; } = new();

    /// <summary>Pomodoro focus timer state.</summary>
    public FocusTimerData FocusTimer { get; init; } = new();
}
