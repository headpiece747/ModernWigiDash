namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Touch actions recognized by the companion task widget hit-testing logic.
/// </summary>
internal enum CozyTodoHitKind
{
    /// <summary>No interactive element hit.</summary>
    None,

    /// <summary>Task checkmark tapped.</summary>
    ToggleTask,

    /// <summary>Feed pet button tapped.</summary>
    FeedPet,

    /// <summary>Pomodoro start/pause button tapped.</summary>
    ToggleFocus,
}
