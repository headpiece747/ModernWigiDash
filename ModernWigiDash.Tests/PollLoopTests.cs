using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class PollLoopTests
{
    // ── Ticking / readiness ──────────────────────────────────

    [TestMethod]
    public async Task Start_Ready_TicksAtInterval()
    {
        // Arrange
        int ticks = 0;
        using var loop = new PollLoop(
            "T", TimeSpan.FromMilliseconds(20),
            ready: () => true,
            tick: () => ticks++,
            onTickFailure: () => { },
            log: _ => { });

        // Act
        loop.Start();
        await TestWait.WaitUntilAsync(() => ticks >= 5, TimeSpan.FromSeconds(2));

        // Assert
        Assert.IsTrue(ticks >= 3, $"Expected at least 3 ticks over ~5 intervals, got {ticks}.");
    }

    [TestMethod]
    public async Task NotReady_PausesAt500ms_ThenTicksWhenReady()
    {
        // Arrange
        bool ready = false;
        int ticks = 0;
        using var loop = new PollLoop(
            "T", TimeSpan.FromMilliseconds(20),
            ready: () => ready,
            tick: () => ticks++,
            onTickFailure: () => { },
            log: _ => { });

        // Act — not ready: the loop pauses at 500ms instead of ticking
        loop.Start();
        await Task.Delay(200);

        // Assert
        Assert.AreEqual(0, ticks, "No tick may fire while not ready.");

        // Act — ready: ticks resume
        ready = true;
        await TestWait.WaitUntilAsync(() => ticks >= 3, TimeSpan.FromSeconds(2));

        // Assert
        Assert.IsTrue(ticks >= 3, $"Expected ticks to resume after readiness, got {ticks}.");
    }

    // ── Lifecycle ────────────────────────────────────────────

    [TestMethod]
    public async Task Stop_MidLoop_CancelsWithoutFurtherTicks()
    {
        // Arrange — the tick blocks until released so Stop lands mid-tick,
        // the narrowest window for a spurious tick after cancellation.
        var tickEntered = new ManualResetEventSlim(false);
        var tickRelease = new ManualResetEventSlim(false);
        int ticks = 0;
        using var loop = new PollLoop(
            "T", TimeSpan.FromMilliseconds(20),
            ready: () => true,
            tick: () => { ticks++; tickEntered.Set(); tickRelease.Wait(); },
            onTickFailure: () => { },
            log: _ => { });

        // Act
        loop.Start();
        Assert.IsTrue(tickEntered.Wait(TimeSpan.FromSeconds(2)), "Loop must reach its first tick.");
        loop.Stop();
        tickRelease.Set();
        await Task.Delay(150);

        // Assert
        Assert.AreEqual(1, ticks, "No tick may fire after Stop.");
    }

    [TestMethod]
    public async Task Dispose_IsIdempotent()
    {
        // Arrange
        int ticks = 0;
        using var loop = new PollLoop(
            "T", TimeSpan.FromMilliseconds(20),
            ready: () => true,
            tick: () => ticks++,
            onTickFailure: () => { },
            log: _ => { });

        // Act — dispose twice (and implicitly Stop) on a running loop
        loop.Start();
        await TestWait.WaitUntilAsync(() => ticks >= 3, TimeSpan.FromSeconds(2));
        loop.Dispose();
        loop.Dispose();
        int ticksAfterDispose = ticks;
        await Task.Delay(150);

        // Assert — repeated Dispose must be safe and must stop ticking
        Assert.AreEqual(ticksAfterDispose, ticks, "No tick may fire after Dispose.");
    }

    [TestMethod]
    public void Dispose_BlocksUntilInFlightTickCompletes()
    {
        // Arrange — the tick blocks on a gate, so Dispose lands mid-tick: the
        // join must hold Dispose until the tick finishes (a caller freeing a
        // resource the tick touches must never return past a live tick), and
        // releasing the gate must let both the tick and Dispose complete.
        var tickEntered = new ManualResetEventSlim(false);
        var tickRelease = new ManualResetEventSlim(false);
        bool tickCompleted = false;
        using var loop = new PollLoop(
            "T", TimeSpan.FromMilliseconds(20),
            ready: () => true,
            tick: () => { tickEntered.Set(); tickRelease.Wait(); tickCompleted = true; },
            onTickFailure: () => { },
            log: _ => { });

        loop.Start();
        Assert.IsTrue(tickEntered.Wait(TimeSpan.FromSeconds(2)), "Loop must reach its first tick.");

        // Act — Dispose from another thread: it must not return past the tick
        var disposeThread = new Thread(() => loop.Dispose()) { IsBackground = true };
        disposeThread.Start();

        // Assert — Dispose is blocked on the in-flight tick (it had 200ms to
        // complete and cannot while the tick gate is held)
        Assert.IsFalse(disposeThread.Join(200), "Dispose must block while a tick is in flight.");

        // Act — release the gate: the tick completes, Dispose joins and returns
        tickRelease.Set();
        Assert.IsTrue(disposeThread.Join(TimeSpan.FromSeconds(2)), "Dispose must return once the tick completes.");

        // Assert
        Assert.IsTrue(tickCompleted, "The in-flight tick must run to completion before Dispose returns.");
    }

    // ── Failure handling / logging dedupe ────────────────────

    [TestMethod]
    public async Task TickFailure_InvokesOnTickFailureAndContinues()
    {
        // Arrange
        int failures = 0;
        using var loop = new PollLoop(
            "T", TimeSpan.FromMilliseconds(20),
            ready: () => true,
            tick: () => throw new InvalidOperationException("boom"),
            onTickFailure: () => failures++,
            log: _ => { });

        // Act — the loop must survive repeated tick failures
        loop.Start();
        await TestWait.WaitUntilAsync(() => failures >= 2, TimeSpan.FromSeconds(2));

        // Assert
        Assert.IsTrue(failures >= 2, $"Loop must keep ticking after a failure, got {failures}.");
    }

    [TestMethod]
    public async Task TickFailure_SameMessage_LoggedOnce()
    {
        // Arrange
        bool fail = true;
        int failures = 0;
        int successes = 0;
        List<string> logged = [];
        using var loop = new PollLoop(
            "T", TimeSpan.FromMilliseconds(20),
            ready: () => true,
            tick: () => { if (fail) { throw new InvalidOperationException("boom"); } successes++; },
            onTickFailure: () => failures++,
            log: logged.Add);

        // Act — repeated failures with the same message
        loop.Start();
        await TestWait.WaitUntilAsync(() => failures >= 2, TimeSpan.FromSeconds(2));

        // Assert — identical messages dedupe to one log line
        Assert.AreEqual(1, logged.Count(msg => msg.Contains("poll failed")), "Repeated identical failures must log once.");

        // Act — a successful tick clears the dedupe state
        fail = false;
        await TestWait.WaitUntilAsync(() => successes >= 1, TimeSpan.FromSeconds(2));
        int failuresAtSuccess = failures;

        // Act — a fresh failure with the same message logs again
        fail = true;
        await TestWait.WaitUntilAsync(() => failures >= failuresAtSuccess + 1, TimeSpan.FromSeconds(2));

        // Assert
        Assert.AreEqual(2, logged.Count(msg => msg.Contains("poll failed")), "A fresh failure after a success must log again.");
    }
}
