namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// Persisted Pomodoro focus timer configuration and state.
/// </summary>
public sealed record FocusTimerData
{
    /// <summary>Whether the timer is actively counting down.</summary>
    public bool IsRunning { get; init; }

    /// <summary>When the timer started or resumed in UTC.</summary>
    public DateTime? StartedAtUtc { get; init; }

    /// <summary>Total duration of the session in seconds (default 1500s = 25m).</summary>
    public double DurationSeconds { get; init; } = 1500;

    /// <summary>Elapsed seconds stored when paused.</summary>
    public double ElapsedSecondsAtPause { get; init; }

    /// <summary>Task ID currently focused on, if any.</summary>
    public string? ActiveTaskId { get; init; }
}
