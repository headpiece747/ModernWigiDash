namespace ModernWigiDash.Tests;

using System.IO;
using ModernWigiDash.Widgets.Todo;

/// <summary>
/// The focus-timer module's pins: the one clamp rule, the start/stop
/// transitions, and the stop reward.
/// </summary>
[TestClass]
public class FocusTimerTests
{
    [TestMethod]
    public void Resolve_Paused_ReturnsThePersistedElapsed()
    {
        var data = new FocusTimerData
        {
            IsRunning = false,
            ElapsedSecondsAtPause = 90,
            DurationSeconds = 1500,
            ActiveTaskId = "task-1",
        };

        var state = FocusTimer.Resolve(data, DateTime.UtcNow);

        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual(TimeSpan.FromSeconds(90), state.Elapsed);
        Assert.AreEqual(TimeSpan.FromSeconds(1500), state.Duration);
        Assert.AreEqual("task-1", state.ActiveTaskId);
    }

    [TestMethod]
    public void Resolve_Running_AccumulatesTheClockDeltaOverThePausedElapsed()
    {
        var clock = new FakeTimeProvider();
        var data = new FocusTimerData
        {
            IsRunning = true,
            StartedAtUtc = clock.GetUtcNow().UtcDateTime,
            ElapsedSecondsAtPause = 60,
            DurationSeconds = 1500,
        };
        clock.Advance(TimeSpan.FromMinutes(1));

        var state = FocusTimer.Resolve(data, clock);

        Assert.IsTrue(state.IsRunning);
        Assert.AreEqual(TimeSpan.FromMinutes(2), state.Elapsed);
    }

    [TestMethod]
    public void Resolve_Running_ClampsTheElapsedAtTheDuration()
    {
        var clock = new FakeTimeProvider();
        var data = FocusTimer.Start(new FocusTimerData(), clock, null);
        clock.Advance(TimeSpan.FromHours(2));

        var state = FocusTimer.Resolve(data, clock);

        Assert.AreEqual(state.Duration, state.Elapsed);
        Assert.AreEqual(TimeSpan.FromSeconds(FocusTimer.DefaultDurationSeconds), state.Duration);
    }

    [TestMethod]
    public void Start_Fresh_SetsRunningTheStartStampAndTheDefaultDuration()
    {
        var clock = new FakeTimeProvider();

        var data = FocusTimer.Start(new FocusTimerData(), clock, "task-1");

        Assert.IsTrue(data.IsRunning);
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime, data.StartedAtUtc);
        Assert.AreEqual(FocusTimer.DefaultDurationSeconds, data.DurationSeconds);
        Assert.AreEqual("task-1", data.ActiveTaskId);
    }

    [TestMethod]
    public void Start_Running_IsANoop()
    {
        var clock = new FakeTimeProvider();
        var running = FocusTimer.Start(new FocusTimerData(), clock, "task-1");

        var result = FocusTimer.Start(running, clock, "task-2");

        Assert.AreSame(running, result);
    }

    [TestMethod]
    public void Start_Resume_KeepsTheAccumulatedElapsedAndTheActiveTask()
    {
        var clock = new FakeTimeProvider();
        var paused = new FocusTimerData
        {
            ElapsedSecondsAtPause = 300,
            DurationSeconds = 1200,
            ActiveTaskId = "kept",
        };

        var data = FocusTimer.Start(paused, clock, null);

        Assert.AreEqual(300, data.ElapsedSecondsAtPause);
        Assert.AreEqual(1200, data.DurationSeconds);
        Assert.AreEqual("kept", data.ActiveTaskId);
    }

    [TestMethod]
    public void Start_BlankDuration_TakesTheDefault()
    {
        var clock = new FakeTimeProvider();

        var data = FocusTimer.Start(new FocusTimerData { DurationSeconds = 0 }, clock, null);

        Assert.AreEqual(FocusTimer.DefaultDurationSeconds, data.DurationSeconds);
    }

    [TestMethod]
    public void Stop_Running_ReturnsZeroedDataAndTheClampedReward()
    {
        var clock = new FakeTimeProvider();
        var running = FocusTimer.Start(new FocusTimerData(), clock, null);
        clock.Advance(TimeSpan.FromHours(2));

        var (stopped, focusXp) = FocusTimer.Stop(running, clock);

        Assert.IsFalse(stopped.IsRunning);
        Assert.IsNull(stopped.StartedAtUtc);
        Assert.AreEqual(0, stopped.ElapsedSecondsAtPause);
        Assert.AreEqual(250, focusXp); // 25m * 10 XP/min, capped at the duration
    }

    [TestMethod]
    public void Stop_NotRunning_IsANoop()
    {
        var clock = new FakeTimeProvider();
        var idle = new FocusTimerData();

        var (data, focusXp) = FocusTimer.Stop(idle, clock);

        Assert.AreSame(idle, data);
        Assert.AreEqual(0, focusXp);
    }

    [TestMethod]
    public async Task StopFocusTimer_CapsElapsedAtDuration()
    {
        string statePath = Path.Combine(Path.GetTempPath(), "wmd-todo-test-" + Guid.NewGuid().ToString("N"), "cozy_state.json");
        var store = new TodoStore(statePath);
        var clock = new FakeTimeProvider();
        var local = new LocalTaskProvider(store, clock);

        await local.StartFocusTimerAsync(null);

        // Advance fake clock by 24 hours (1440 minutes)
        clock.Advance(TimeSpan.FromHours(24));

        await local.StopFocusTimerAsync();

        var companion = local.GetCompanionState();
        // A 25m focus timer should award at most 250 XP (25m * 10 XP/min), not 14,400 XP!
        Assert.AreEqual(250, companion.TotalXp, "Focus session XP must be capped at timer duration");
        Assert.AreEqual(25, companion.SnackTreats);

        if (Directory.Exists(Path.GetDirectoryName(statePath)))
        {
            Directory.Delete(Path.GetDirectoryName(statePath)!, true);
        }
    }
}
