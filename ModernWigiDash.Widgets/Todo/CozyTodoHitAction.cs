namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Result of hit-testing a touch point against the companion task layout.
/// </summary>
internal readonly record struct CozyTodoHitAction(
    CozyTodoHitKind Kind,
    string? TaskId = null)
{
    /// <summary>Static instance representing no hit.</summary>
    public static readonly CozyTodoHitAction None = new(CozyTodoHitKind.None);

    /// <summary>Creates a task toggle hit action.</summary>
    public static CozyTodoHitAction Toggle(string taskId) => new(CozyTodoHitKind.ToggleTask, taskId);

    /// <summary>Creates a feed pet hit action.</summary>
    public static readonly CozyTodoHitAction Feed = new(CozyTodoHitKind.FeedPet);

    /// <summary>Creates a focus timer toggle hit action.</summary>
    public static readonly CozyTodoHitAction Focus = new(CozyTodoHitKind.ToggleFocus);
}
