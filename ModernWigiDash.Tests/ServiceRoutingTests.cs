using System.Threading;
using ModernWigiDash.App.ServiceRouting;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class ServiceRoutingTests
{
    // ── ServiceRoutingState ─────────────────────────────────

    [TestMethod]
    public void ServiceRoutingState_StartsInactive()
    {
        var state = new ServiceRoutingState();

        Assert.IsFalse(state.IsServiceActive);
    }

    [TestMethod]
    public void ServiceRoutingState_MarkActive_Activates()
    {
        var state = new ServiceRoutingState(failureThreshold: 2);
        state.MarkActive();

        Assert.IsTrue(state.IsServiceActive);

        // Failures accumulated before activation must not flip the new session.
        state.ReportFailure();
        Assert.IsTrue(state.IsServiceActive);
    }

    [TestMethod]
    public void ServiceRoutingState_BelowThreshold_StaysActive()
    {
        var state = new ServiceRoutingState(failureThreshold: 3);
        state.MarkActive();

        state.ReportFailure();
        state.ReportFailure();

        Assert.IsTrue(state.IsServiceActive, "Failures below the threshold must not flip readiness");
    }

    [TestMethod]
    public void ServiceRoutingState_AtThreshold_FlipsInactiveAndTriggersReconnect()
    {
        int reconnects = 0;
        var state = new ServiceRoutingState(failureThreshold: 2, onReconnect: () => reconnects++);
        state.MarkActive();

        state.ReportFailure();
        state.ReportFailure();

        Assert.IsFalse(state.IsServiceActive);
        Assert.AreEqual(1, reconnects);
    }

    [TestMethod]
    public void ServiceRoutingState_ReconnectTrigger_IsThrottled()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        int reconnects = 0;
        var state = new ServiceRoutingState(
            failureThreshold: 1,
            retryInterval: TimeSpan.FromSeconds(10),
            onReconnect: () => reconnects++,
            timeProvider: clock);
        state.MarkActive();

        // First trip triggers; second trip within the interval must not.
        state.ReportFailure();
        state.MarkActive();
        state.ReportFailure();
        Assert.AreEqual(1, reconnects, "Re-detect must be throttled to once per interval");

        // After the interval elapses, another trip triggers again.
        clock.Advance(TimeSpan.FromSeconds(11));
        state.MarkActive();
        state.ReportFailure();
        Assert.AreEqual(2, reconnects);
    }

    [TestMethod]
    public void ServiceRoutingState_FailuresWhileInactive_DoNotRetrigger()
    {
        int reconnects = 0;
        var state = new ServiceRoutingState(failureThreshold: 1, onReconnect: () => reconnects++);
        state.MarkActive();
        state.ReportFailure(); // flips inactive + triggers

        state.ReportFailure();
        state.ReportFailure();

        Assert.AreEqual(1, reconnects, "Failures while already inactive must not re-trigger re-detect");
    }

    // ── WcfPollLoop ─────────────────────────────────────────

    [TestMethod]
    public void WcfPollLoop_WhenReady_InvokesTick()
    {
        using var ticked = new ManualResetEventSlim(false);
        var loop = new PollLoop(
            "TEST", TimeSpan.FromMilliseconds(5),
            ready: () => true,
            tick: () => ticked.Set(),
            onTickFailure: () => { },
            log: _ => { });

        loop.Start();

        Assert.IsTrue(ticked.Wait(TimeSpan.FromSeconds(3)), "The loop must invoke the tick when ready");
        loop.Stop();
    }

    [TestMethod]
    public async Task WcfPollLoop_WhenNotReady_DoesNotInvokeTick()
    {
        var ticks = 0;
        var loop = new PollLoop(
            "TEST", TimeSpan.FromMilliseconds(5),
            ready: () => false,
            tick: () => ticks++,
            onTickFailure: () => { },
            log: _ => { });

        loop.Start();
        await Task.Delay(200);
        loop.Stop();

        Assert.AreEqual(0, ticks, "The loop must pause while not ready");
    }

    [TestMethod]
    public void WcfPollLoop_TickFailure_InvokesFailureObserver()
    {
        using var failed = new ManualResetEventSlim(false);
        var loop = new PollLoop(
            "TEST", TimeSpan.FromMilliseconds(5),
            ready: () => true,
            tick: () => throw new InvalidOperationException("boom"),
            onTickFailure: () => failed.Set(),
            log: _ => { });

        loop.Start();

        Assert.IsTrue(failed.Wait(TimeSpan.FromSeconds(3)), "A failing tick must reach the failure observer");
        loop.Stop();
    }

    [TestMethod]
    public async Task WcfPollLoop_StartTwice_IsIdempotent()
    {
        int ticks = 0;
        var loop = new PollLoop(
            "TEST", TimeSpan.FromMilliseconds(5),
            ready: () => true,
            tick: () => ticks++,
            onTickFailure: () => { },
            log: _ => { });

        loop.Start();
        loop.Start();
        await Task.Delay(100);
        loop.Stop();

        Assert.IsTrue(ticks > 0, "The loop must run");
        Assert.IsTrue(ticks < 100, "A double start must not double the tick rate");
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
