using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// The shared named-map acquire policy (2026-09-27, the architecture review's
/// "one mutex-acquire policy behind both named-map adapters"). Both producers'
/// adapters used to carry their own copy of the budget, the abandoned-mutex rule
/// and the diagnostic, and the two diagnostics had already drifted apart in
/// spelling ("100ms" vs "100 ms") while each retyped a number its own constant
/// already held.
///
/// The message format is pinned HERE, literally, because this is the one place
/// that owns it. The adapter tests assert the opposite thing - that each adapter
/// routes to this policy rather than spelling its own - so neither test is
/// comparing a value with itself.
/// </summary>
[TestClass]
public class NamedMapAcquirePolicyTests
{
    [TestMethod]
    public void Timeout_IsTheHundredMillisecondFrameBudget()
    {
        // A per-frame poll of somebody else's live telemetry: a writer holding
        // the mutex longer than one frame will not release it inside the next
        // one either, so a longer wait would only make the panel staler.
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), NamedMapAcquirePolicy.Timeout);
    }

    [TestMethod]
    public void NotAcquiredMessage_StatesTheBudgetInMilliseconds()
    {
        // The literal, in the one place that owns the format. The number comes
        // from the budget, so this cannot say 100 while the policy waits 250.
        Assert.AreEqual(
            "LHS sensor mutex not acquired within 100 ms (writer holds it)",
            NamedMapAcquirePolicy.NotAcquiredMessage("LHS sensor mutex"));
    }

    [TestMethod]
    public void TryAcquire_Uncontended_TakesTheMutex()
    {
        using var mutex = new Mutex();

        Assert.IsTrue(NamedMapAcquirePolicy.TryAcquire(mutex), "a free mutex must be acquired");
        NamedMapAcquirePolicy.Release(mutex);
    }

    [TestMethod]
    public void TryAcquire_HeldByAnotherThread_SpendsTheBudgetAndReportsNotAcquired()
    {
        using var mutex = new Mutex();
        using var held = new ManualResetEventSlim(false);
        using var releaseHolder = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            mutex.WaitOne();
            held.Set();
            // Hold until the test releases us, so TryAcquire below spends its
            // whole budget against a mutex that is provably still taken. (No
            // sleep: the holder's release is the event, not the clock.)
            releaseHolder.Wait();
            mutex.ReleaseMutex();
        })
        { IsBackground = true };
        holder.Start();
        Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(5)), "the holder thread never took the mutex");

        try
        {
            Assert.IsFalse(
                NamedMapAcquirePolicy.TryAcquire(mutex),
                "a mutex the writer still holds must report NOT acquired once the budget is spent");
        }
        finally
        {
            releaseHolder.Set();
            holder.Join(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public void TryAcquire_AbandonedByADeadWriter_ReportsAcquired()
    {
        // Only the producer writes these maps, so a producer that died holding
        // the mutex leaves it abandoned rather than locked - safe to continue
        // under. The abandoned case is the reason this is a policy and not a bare
        // WaitOne at each call site: it is the one outcome that must NOT read as
        // a failure, and it is invisible until a producer actually dies.
        using var mutex = new Mutex();
        var dead = new Thread(() =>
        {
            mutex.WaitOne();
            // Return WITHOUT releasing: the handle dies holding the mutex, which
            // is exactly what abandonment is.
        })
        { IsBackground = true };
        dead.Start();
        Assert.IsTrue(dead.Join(TimeSpan.FromSeconds(5)), "the dead-writer thread never finished");

        Assert.IsTrue(
            NamedMapAcquirePolicy.TryAcquire(mutex),
            "an abandoned mutex must read as ACQUIRED - only the producer writes this map");
        NamedMapAcquirePolicy.Release(mutex);
    }

    [TestMethod]
    public void Release_NotHeldByThisThread_SwallowsTheApplicationException()
    {
        // The producer may exit while we hold the mutex; releasing then throws
        // ApplicationException, which is not a failure of the read that already
        // succeeded. A release that propagated would turn a good frame into an
        // error report.
        using var mutex = new Mutex();

        NamedMapAcquirePolicy.Release(mutex);

        Assert.IsTrue(
            mutex.WaitOne(0),
            "a release of a mutex this thread never held must leave it acquirable - swallowing the exception is right, corrupting the mutex is not");
        mutex.ReleaseMutex();
    }
}
