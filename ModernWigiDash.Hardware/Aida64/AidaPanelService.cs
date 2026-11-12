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
    private static readonly ManualResetEventSlim Idle = new(initialState: true);
    private static AidaPanelMaster? _shared;
    private static int _borrowers;
    private static bool _releasing;

    /// <summary>Borrows the process-wide master, creating it on first use.</summary>
    public static AidaPanelMaster Acquire()
    {
        while (true)
        {
            lock (Gate)
            {
                if (_shared is not null)
                {
                    _borrowers++;
                    return _shared;
                }

                // The decision and the creation are one critical section: a
                // release clears _shared and raises _releasing under the same
                // gate, so a master is only created when no release is in
                // flight. Slot 0 is one slot; two masters would clear each
                // other's frame markers.
                if (!_releasing)
                {
                    _shared = CreateProduction();
                    _borrowers = 1;
                    return _shared;
                }
            }

            // A release is disposing the previous master (its loop join is
            // bounded at 5 s). Wait for it, then re-check under the gate.
            Idle.Wait(TimeSpan.FromSeconds(6));
        }
    }

    /// <summary>Releases a borrow; the last release disposes the master, stopping its loop.</summary>
    public static void Release()
    {
        AidaPanelMaster? toDispose = null;
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
                _releasing = true;
                Idle.Reset();
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
            lock (Gate)
            {
                _releasing = false;
            }

            Idle.Set();
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
}
