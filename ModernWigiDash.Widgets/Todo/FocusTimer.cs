namespace ModernWigiDash.Widgets.Todo;

/// <summary>
/// The one owner of the focus-timer concept: the single elapsed-time
/// clamp rule, the start/stop transitions, and the stop reward. Both
/// <see cref="ITaskProvider"/> implementations delegate here, so the
/// focus-timer knowledge has one home and the Local / Super Productivity
/// divergence (two duration sources, two stop rules, four clamp copies)
/// is unrepresentable.
/// </summary>
internal static class FocusTimer
{
    /// <summary>The default session length in seconds (25 minutes).</summary>
    public const double DefaultDurationSeconds = 1500;

    /// <summary>
    /// The current view of the timer state: the one clamp rule applied to
    /// the clock read. Paused, the elapsed is the persisted value;
    /// running, the clock-derived delta over the accumulated elapsed,
    /// clamped to the duration.
    /// </summary>
    public static FocusState Resolve(FocusTimerData data, DateTime nowUtc)
    {
        TimeSpan duration = TimeSpan.FromSeconds(data.DurationSeconds);
        if (!data.IsRunning || data.StartedAtUtc is not { } startedAtUtc)
        {
            return new FocusState(
                IsRunning: false,
                Elapsed: TimeSpan.FromSeconds(data.ElapsedSecondsAtPause),
                Duration: duration,
                ActiveTaskId: data.ActiveTaskId);
        }

        TimeSpan elapsed = nowUtc - startedAtUtc + TimeSpan.FromSeconds(data.ElapsedSecondsAtPause);
        if (elapsed > duration)
        {
            elapsed = duration;
        }

        return new FocusState(
            IsRunning: true,
            Elapsed: elapsed,
            Duration: duration,
            ActiveTaskId: data.ActiveTaskId);
    }

    /// <summary>The <see cref="Resolve(FocusTimerData, DateTime)"/> view at the provider's clock.</summary>
    public static FocusState Resolve(FocusTimerData data, TimeProvider clock)
        => Resolve(data, clock.GetUtcNow().UtcDateTime);

    /// <summary>
    /// Starts (or resumes) the timer. A resume preserves the accumulated
    /// elapsed; a start while already running is a no-op (it never resets a
    /// session in flight). A blank persisted duration takes the default.
    /// </summary>
    public static FocusTimerData Start(FocusTimerData data, TimeProvider clock, string? taskId)
    {
        if (data.IsRunning)
        {
            return data;
        }

        double durationSeconds = data.DurationSeconds > 0 ? data.DurationSeconds : DefaultDurationSeconds;
        return data with
        {
            IsRunning = true,
            StartedAtUtc = clock.GetUtcNow().UtcDateTime,
            DurationSeconds = durationSeconds,
            ActiveTaskId = taskId ?? data.ActiveTaskId,
        };
    }

    /// <summary>
    /// Stops the timer: the clamped elapsed and the companion reward
    /// computed through the engine (the one stop rule). Stopping a timer
    /// that is not running is a no-op (the same data instance returns).
    /// </summary>
    public static (FocusTimerData Data, int FocusXp) Stop(FocusTimerData data, TimeProvider clock)
    {
        if (!data.IsRunning || data.StartedAtUtc is not { } startedAtUtc)
        {
            return (data, 0);
        }

        TimeSpan duration = TimeSpan.FromSeconds(data.DurationSeconds);
        TimeSpan elapsed = clock.GetUtcNow().UtcDateTime - startedAtUtc + TimeSpan.FromSeconds(data.ElapsedSecondsAtPause);
        if (elapsed > duration)
        {
            elapsed = duration;
        }

        return (
            data with
            {
                IsRunning = false,
                StartedAtUtc = null,
                ElapsedSecondsAtPause = 0,
            },
            CozyCompanionEngine.CalculateFocusXp(elapsed));
    }
}
