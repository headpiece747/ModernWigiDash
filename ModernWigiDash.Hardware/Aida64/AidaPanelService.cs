using ModernWigiDash.Hardware.Service;

namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// The AIDA64 panel master's process-wide owner and production wiring. There
/// must be exactly ONE master per process: the vendor's slot 0 is a single slot,
/// and two masters on it would clear each other's frame markers. Every AIDA64
/// widget borrows the same instance (ref-counted; the poll loop stops when the
/// last borrower releases it). The map is opened lazily on the first poll.
/// </summary>
public static class AidaPanelService
{
    private static readonly System.Threading.Lock Gate = new();
    private static Task _activeRelease = Task.CompletedTask;
    private static AidaPanelMaster? _shared;
    private static int _borrowers;

    /// <summary>Borrows the process-wide master, creating it on first use without
    /// blocking the UI thread if a previous master is still releasing slot 0.</summary>
    public static AidaPanelMaster Acquire() => Acquire(CreateProductionWithPriorRelease);

    /// <summary>Test seam: borrows using the supplied factory receiving the prior
    /// release completion task so slow releases stay off the caller's thread.</summary>
    internal static AidaPanelMaster Acquire(Func<Task, AidaPanelMaster> factory)
    {
        lock (Gate)
        {
            if (_shared is not null)
            {
                _borrowers++;
                return _shared;
            }

            if (_activeRelease.IsCompleted)
            {
                _activeRelease = Task.CompletedTask;
            }

            _shared = factory(_activeRelease);
            _borrowers = 1;
            return _shared;
        }
    }

    /// <summary>Releases a borrow; the last release disposes the master, stopping its loop.</summary>
    public static void Release()
    {
        AidaPanelMaster? toDispose = null;
        TaskCompletionSource? currentRelease = null;
        lock (Gate)
        {
            if (_borrowers == 0)
            {
                return;
            }

            if (--_borrowers == 0)
            {
                toDispose = _shared;
                _shared = null;
                currentRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _activeRelease = _activeRelease.IsCompleted
                    ? currentRelease.Task
                    : Task.WhenAll(_activeRelease, currentRelease.Task);
            }
        }

        if (toDispose is null)
        {
            return;
        }

        try
        {
            toDispose.Dispose();
        }
        finally
        {
            Task slotReleased = toDispose.SlotReleasedTask;
            if (slotReleased.IsCompleted)
            {
                currentRelease!.TrySetResult();
            }
            else
            {
                // Keep waiting until any hung WCF slot-0 call that outlived
                // PollLoop's 5s join timeout unwinds and drops _inFlightSlotWork.
                _ = slotReleased.ContinueWith(
                    static (_, state) => ((TaskCompletionSource)state!).TrySetResult(),
                    currentRelease!,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    /// <summary>
    /// Creates the production master: the vendor's named map for the frame reads
    /// and the vendor WCF service for the small handshake writes (the map's own
    /// handle is opened read-only, and the vendor's mutex is denied to a
    /// non-elevated process).
    /// </summary>
    /// <param name="log">The diagnostic sink; defaults to <see cref="FileLog"/>.</param>
    public static AidaPanelMaster CreateProduction(Action<string>? log = null)
        => new(new MemoryMappedAidaMmapSource(), new VendorAidaMmapWriter(), log);

    private static AidaPanelMaster CreateProductionWithPriorRelease(Task priorRelease)
        => new(new MemoryMappedAidaMmapSource(), new VendorAidaMmapWriter(), priorRelease);
}
