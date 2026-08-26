using System.Threading;

namespace ModernWigiDash.App;

/// <summary>
/// The single-instance guard: the named mutex (the instance claim) plus the
/// named activation event (the second launch's "show yourself" signal). The
/// primary owns the handles for the process lifetime; the kernel releases
/// them on process death (clean exit, crash, or force-kill), so a dead
/// instance can never wedge the next launch. The secondary finds the mutex
/// already claimed, signals the activation event (staying set, manual reset,
/// until the primary consumes it), and the caller exits. The handle
/// factories are injected, so the verdict + signal policy is drivable in
/// tests with in-memory handles (the MemoryMappedLhmMapSource seam
/// precedent). Per-session handle scope, deliberately: each user session
/// may run its own instance, and the tray icon is per-session anyway.
/// </summary>
internal sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>The instance-claim mutex name (per-session scope).</summary>
    public const string MutexName = "ModernWigiDash.SingleInstance";

    /// <summary>The activation event name: a secondary launch signals it to
    /// make the primary show its window.</summary>
    public const string EventName = "ModernWigiDash.Activate";

    /// <summary>True when this process owns the instance (the primary);
    /// false when an earlier instance holds the claim and the activation
    /// signal was sent (the secondary: the caller must exit).</summary>
    public bool IsPrimary { get; }

    private readonly Mutex _mutex;
    private readonly EventWaitHandle? _activation;
    private readonly Action _onActivate;
    private RegisteredWaitHandle? _registration;
    private int _disposed;

    /// <summary>Production entry point: the real named handles.</summary>
    public SingleInstanceGuard(Action onActivate)
        : this(onActivate, ProductionHandles())
    {
    }

    private static GuardHandleFactory ProductionHandles() => new(
        AcquireMutex: AcquireClaimMutex,
        CreateEvent: () => new EventWaitHandle(false, EventResetMode.ManualReset, EventName),
        OpenEvent: () => EventWaitHandle.OpenExisting(EventName));

    private static (Mutex Mutex, bool CreatedNew) AcquireClaimMutex()
        => (new Mutex(initiallyOwned: false, MutexName, out bool createdNew), createdNew);

    /// <summary>Test seam: the handle factories are injected, so the verdict
    /// and signal policy run against in-memory handles.</summary>
    internal SingleInstanceGuard(Action onActivate, GuardHandleFactory handles)
    {
        (Mutex mutex, bool createdNew) = handles.AcquireMutex();
        _mutex = mutex;
        _onActivate = onActivate;
        IsPrimary = createdNew;
        if (createdNew)
        {
            _activation = handles.CreateEvent();
            ParkActivationWait();
        }
        else
        {
            // Tell the primary to show itself. The event is manual reset, so
            // the signal stays set if the primary is still mid-startup and
            // parks its wait after us — RegisterWaitForSingleObject observes
            // the already-signaled state and fires immediately. A vanished
            // event (the primary died between claiming the mutex and
            // creating the event) is logged, not fatal: this launch exits
            // either way.
            try
            {
                var opened = handles.OpenEvent();
                using (opened)
                {
                    opened.Set();
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"[App] Second launch could not signal the running instance: {ex.Message}");
            }
        }
    }

    /// <summary>The handle factories behind the guard: acquire the claim
    /// mutex (returning whether this process created it), create the
    /// activation event (the primary), and open it (the secondary).</summary>
    internal sealed record GuardHandleFactory(
        Func<(Mutex Mutex, bool CreatedNew)> AcquireMutex,
        Func<EventWaitHandle> CreateEvent,
        Func<EventWaitHandle> OpenEvent);

    /// <summary>The primary's wait: park a one-shot registration on the
    /// activation event. A secondary launch's signal fires
    /// <see cref="OnActivationSignaled"/> on a thread-pool thread (the
    /// window's wiring hops to its dispatcher).</summary>
    private void ParkActivationWait()
    {
        // _activation is non-null here: the primary branch assigns it before
        // the first park, and the only other caller (the one-shot re-park)
        // runs only on the primary.
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activation!, new WaitOrTimerCallback(OnActivationSignaled), null, Timeout.Infinite, executeOnlyOnce: true);
    }

#pragma warning disable S1172 // The state/timedOut parameters are the WaitOrTimerCallback shape; the signal carries both.
    private void OnActivationSignaled(object? state, bool timedOut)
#pragma warning restore S1172
    {
        // One-shot registration: re-park BEFORE firing, so a fast second
        // secondary launch cannot lose its activation, then consume the
        // manual-reset signal and hand it to the window.
        ParkActivationWait();
        _activation?.Reset();
        _onActivate();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _registration?.Unregister(null);
        _registration = null;
        _activation?.Dispose();
        _mutex.Dispose();
    }
}
