using System.Threading;

namespace ModernWigiDash.Tests;

/// <summary>
/// The single-instance guard's verdict and signal policy, pinned against
/// in-memory handles through the injected factory seam (no second process):
/// the first claim is primary, a second claim is secondary and signals the
/// activation event, the primary fires its callback on the signal (and
/// re-parks for the next one), and the handles release on dispose.
/// </summary>
[TestClass]
public class SingleInstanceGuardTests
{
    [TestMethod]
    public void Acquire_NoRunningInstance_IsPrimary()
    {
        var handles = InMemoryHandles(UniqueName("mutex"), UniqueName("event"));

        using var guard = new SingleInstanceGuard(() => { }, handles);

        Assert.IsTrue(guard.IsPrimary, "the first claim owns the instance");
    }

    [TestMethod]
    public void Acquire_RunningInstance_IsSecondary()
    {
        // The "running instance": a held claim mutex plus a live activation
        // event (the primary's kernel objects, held directly here).
        string mutexName = UniqueName("mutex");
        string eventName = UniqueName("event");
        using var heldClaim = new Mutex(initiallyOwned: true, mutexName);
        using var primaryEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);

        using var guard = new SingleInstanceGuard(() => { }, InMemoryHandles(mutexName, eventName));

        Assert.IsFalse(guard.IsPrimary, "a second claim against a held mutex is the secondary");
    }

    [TestMethod]
    public void SecondaryLaunch_SignalsThePrimaryActivationEvent()
    {
        string mutexName = UniqueName("mutex");
        string eventName = UniqueName("event");
        using var heldClaim = new Mutex(initiallyOwned: true, mutexName);
        using var primaryEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);

        using var guard = new SingleInstanceGuard(() => { }, InMemoryHandles(mutexName, eventName));

        // The signal is manual reset: it stays set until the primary
        // consumes it, so the primary's registration (parked here after the
        // fact, the mid-startup shape) still observes it.
        var registration = ThreadPool.RegisterWaitForSingleObject(primaryEvent, (_, _) => { }, null, Timeout.Infinite, true);
        Assert.IsTrue(primaryEvent.WaitOne(2000),
            "the secondary's signal must be observable on the primary's event");
        registration.Unregister(null);
    }

    [TestMethod]
    public async Task Primary_ActivationSignal_FiresTheCallbackAndReParks()
    {
        var handles = InMemoryHandles(UniqueName("mutex"), UniqueName("event"));
        int activations = 0;
        using var guard = new SingleInstanceGuard(() => Interlocked.Increment(ref activations), handles);

        // Two "second launches": open the event and signal it.
        SignalActivation(handles);
        SignalActivation(handles);

        // The callbacks fire on thread-pool threads: the house's async poll
        // (the Thread.Sleep-loop replacement) waits for both. Five seconds
        // is the house TestWait budget - under the parallel test load a
        // tighter window flakes on a slow scheduler.
        await TestWait.WaitUntilAsync(() => Volatile.Read(ref activations) >= 2, TimeSpan.FromSeconds(5));
        Assert.AreEqual(2, Volatile.Read(ref activations),
            "both activations must fire the callback — the one-shot registration re-parks for the second signal");
    }

    [TestMethod]
    public void Primary_MidStartupSignal_IsNotLost()
    {
        // The race the manual-reset mode exists for: the secondary signals
        // BEFORE the primary parks its wait. The signal stays set, and
        // RegisterWaitForSingleObject observes the already-signaled state
        // and fires.
        string mutexName = UniqueName("mutex");
        string eventName = UniqueName("event");
        using var heldClaim = new Mutex(initiallyOwned: true, mutexName);
        using var primaryEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);

        // The secondary signals now (the event is set and stays set).
        using var secondary = new SingleInstanceGuard(() => { }, InMemoryHandles(mutexName, eventName));
        Assert.IsFalse(secondary.IsPrimary);

        // The primary parks its wait AFTER the signal: the callback must
        // still fire.
        var fired = new ManualResetEvent(false);
        var registration = ThreadPool.RegisterWaitForSingleObject(primaryEvent, (_, _) => fired.Set(), null, Timeout.Infinite, true);
        Assert.IsTrue(fired.WaitOne(2000),
            "a signal landing before the primary parks must not be lost");
        registration.Unregister(null);
    }

    [TestMethod]
    public void Dispose_ThenDisposeAgain_LeavesTheHandlesClosed()
    {
        string mutexName = UniqueName("mutex");
        string eventName = UniqueName("event");
        var guard = new SingleInstanceGuard(() => { }, InMemoryHandles(mutexName, eventName));

        // Double dispose: the second call must be a safe no-op (no throw).
        guard.Dispose();
        guard.Dispose();

        bool reopens = true;
        try
        {
            using var reopened = EventWaitHandle.OpenExisting(eventName);
        }
        catch (Exception)
        {
            reopens = false;
        }
        Assert.IsFalse(reopens,
            "a disposed guard must have closed its handle — the kernel event is unopenable");
    }

    private static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>In-memory handle factories over real (uniquely named)
    /// kernel objects: the seam's test shape, the production adapter's exact
    /// handle calls.</summary>
    private static SingleInstanceGuard.GuardHandleFactory InMemoryHandles(string mutexName, string eventName)
    {
        (Mutex Mutex, bool CreatedNew) acquire()
            => (new Mutex(initiallyOwned: false, mutexName, out bool createdNew), createdNew);
        return new SingleInstanceGuard.GuardHandleFactory(
            AcquireMutex: acquire,
            CreateEvent: () => new EventWaitHandle(false, EventResetMode.ManualReset, eventName),
            OpenEvent: () => EventWaitHandle.OpenExisting(eventName));
    }

    private static void SignalActivation(SingleInstanceGuard.GuardHandleFactory handles)
    {
        var opened = handles.OpenEvent();
        using (opened)
        {
            opened.Set();
        }
    }
}
