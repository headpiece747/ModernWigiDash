namespace ModernWigiDash.Tests;

/// <summary>
/// The <see cref="RestPollLoop"/> cycle module: delay-first cadence through
/// the injected delay delegate, per-symbol failure isolation, the batch-tail
/// hook, and the live membership view (read once per cycle, so late symbols
/// join and released symbols leave) — driven with a fake clock so no real
/// seconds elapse.
/// </summary>
[TestClass]
public class RestPollLoopTests
{
    /// <summary>A delay driven by the fake clock's timers, so
    /// <see cref="FakeTimeProvider.Advance"/> controls the cycle's cadence
    /// deterministically. The cancellation registration is deliberately kept
    /// alive (no using): a pending delay must be cancellable so a token
    /// cancel lets the loop unwind instead of hanging on an un-fired timer.</summary>
    private static Func<TimeSpan, CancellationToken, Task> FakeClockDelay(FakeTimeProvider clock)
        => (delay, ct) =>
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => tcs.TrySetCanceled(ct));
            clock.CreateTimer(_ => tcs.TrySetResult(), null, delay, Timeout.InfiniteTimeSpan);
            return tcs.Task;
        };

    [TestMethod]
    public async Task RunAsync_BadSymbol_DoesNotKillTheLoop()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        using var cts = new CancellationTokenSource();
        var polled = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Batch-completion signals make the advance sequencing deterministic:
        // the loop creates its next delay timer synchronously after
        // afterBatch completes, so advancing only after the signal can never
        // miss it.
        Task loop = RestPollLoop.RunAsync(
            TimeSpan.FromSeconds(30),
            () => true,
            cts.Token,
            () => ["AAPL", "BAD"],
            sym =>
            {
                polled.Enqueue(sym);
                return sym == "BAD" ? Task.FromException(new InvalidOperationException("boom")) : Task.CompletedTask;
            },
            FakeClockDelay(clock),
            new DiagLog("TEST-REST", 100, write: _ => { }),
            () =>
            {
                if (polled.Count == 2) firstBatch.TrySetResult();
                if (polled.Count == 4) secondBatch.TrySetResult();
                return Task.CompletedTask;
            });

        Assert.AreEqual(0, polled.Count, "delay-first: no polls before the window elapses");
        clock.Advance(TimeSpan.FromSeconds(30));
        await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(30));
        await secondBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, polled.Count(p => p == "AAPL"), "the good symbol survives the bad one's failures");
        Assert.AreEqual(2, polled.Count(p => p == "BAD"), "the bad symbol is still attempted every cycle");
        // S6966 suppressed: sync cancel — CancelAsync would dispose the source the loop's pending delay still references.
#pragma warning disable S6966
        cts.Cancel();
#pragma warning restore S6966
        await loop;
    }

    [TestMethod]
    public async Task RunAsync_AfterBatch_RunsOncePerCycleAfterAllSymbols()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        using var cts = new CancellationTokenSource();
        var sequence = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int batches = 0;
        var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task loop = RestPollLoop.RunAsync(
            TimeSpan.FromSeconds(30),
            () => true,
            cts.Token,
            () => ["A", "B"],
            sym =>
            {
                sequence.Enqueue(sym);
                return Task.CompletedTask;
            },
            FakeClockDelay(clock),
            new DiagLog("TEST-REST", 100, write: _ => { }),
            () =>
            {
                sequence.Enqueue("batch");
                batches++;
                if (batches == 1) firstBatch.TrySetResult();
                if (batches == 2) secondBatch.TrySetResult();
                return Task.CompletedTask;
            });

        clock.Advance(TimeSpan.FromSeconds(30));
        await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(30));
        await secondBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "A", "B", "batch", "A", "B", "batch" }, sequence.ToArray(),
            "each cycle must poll every symbol, then run afterBatch");
        // S6966 suppressed: same disposed-CTS hazard as the isolation test above.
#pragma warning disable S6966
        cts.Cancel();
#pragma warning restore S6966
        await loop;
    }

    [TestMethod]
    public async Task RunAsync_MembershipChangesBetweenCycles_PollsTheCurrentSet()
    {
        // The view delegate is read once per cycle, after the delay: a symbol
        // added while the loop is parked is polled on the next cycle, and a
        // released symbol stops being polled. A list captured at start would
        // freeze the set and starve late symbols.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        using var cts = new CancellationTokenSource();
        string[] members = ["A"];
        var sequence = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int cycles = 0;
        var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task loop = RestPollLoop.RunAsync(
            TimeSpan.FromSeconds(30),
            () => true,
            cts.Token,
            () => members,
            sym =>
            {
                sequence.Enqueue(sym);
                return Task.CompletedTask;
            },
            FakeClockDelay(clock),
            new DiagLog("TEST-REST", 100, write: _ => { }),
            () =>
            {
                cycles++;
                if (cycles == 1) firstBatch.TrySetResult();
                if (cycles == 2) secondBatch.TrySetResult();
                if (cycles == 3) thirdBatch.TrySetResult();
                return Task.CompletedTask;
            });

        clock.Advance(TimeSpan.FromSeconds(30));
        await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));

        members = ["A", "B"]; // B subscribes while the loop is parked
        clock.Advance(TimeSpan.FromSeconds(30));
        await secondBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));

        members = ["B"]; // A is released while the loop is parked
        clock.Advance(TimeSpan.FromSeconds(30));
        await thirdBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));

        CollectionAssert.AreEqual(new[] { "A", "A", "B", "B" }, sequence.ToArray(),
            "each cycle must poll the membership as it is at that cycle: late symbols join, released symbols leave");
        // S6966 suppressed: same disposed-CTS hazard as the isolation test above.
#pragma warning disable S6966
        cts.Cancel();
#pragma warning restore S6966
        await loop;
    }
}
