namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Pre-computed layout geometry for the Cozy Companion task widget, shared by rendering and touch hit-testing.
/// </summary>
internal readonly record struct CozyTodoGeometry(
    SKRect Bounds,
    bool IsMinimalist,
    SKRect CompanionCardRect,
    SKRect PetSpriteRect,
    SKRect FeedButtonRect,
    SKRect PomodoroCardRect,
    SKRect PomodoroButtonRect,
    SKRect TasksPanelRect,
    IReadOnlyList<TaskRowGeometry> TaskRows,
    float ScrollOffset,
    float MaxScrollOffset);
