namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// The Pomodoro focus-timer face of the task provider.
/// </summary>
public interface IFocusTimerSource
{
    /// <summary>Gets current Pomodoro focus timer state.</summary>
    ValueTask<FocusState> GetFocusStateAsync(CancellationToken ct = default);

    /// <summary>Starts or resumes the Pomodoro focus timer.</summary>
    ValueTask StartFocusTimerAsync(string? taskId, CancellationToken ct = default);

    /// <summary>Stops or pauses the Pomodoro focus timer.</summary>
    ValueTask StopFocusTimerAsync(CancellationToken ct = default);
}
