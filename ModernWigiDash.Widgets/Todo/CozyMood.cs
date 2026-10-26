namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Companion virtual pet mood state.
/// </summary>
public enum CozyMood
{
    /// <summary>Idle state when no focus timer is running and no tasks were recently finished.</summary>
    Sleepy,

    /// <summary>Attentive state when a Pomodoro focus timer is active.</summary>
    Attentive,

    /// <summary>Happy state when tasks are completed or streak is maintained.</summary>
    Happy,

    /// <summary>Overjoyed celebratory state when fed treats.</summary>
    Overjoyed,
}
