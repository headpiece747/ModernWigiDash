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
    private static AidaPanelMaster? _shared;
    private static int _borrowers;

    /// <summary>Borrows the process-wide master, creating it on first use.</summary>
    public static AidaPanelMaster Acquire()
    {
        lock (Gate)
        {
            _shared ??= CreateProduction();
            _borrowers++;
            return _shared;
        }
    }

    /// <summary>Releases a borrow; the last release disposes the master, stopping its loop.</summary>
    public static void Release()
    {
        AidaPanelMaster? toDispose = null;
        lock (Gate)
        {
            if (_borrowers > 0 && --_borrowers == 0)
            {
                toDispose = _shared;
                _shared = null;
            }
        }

        toDispose?.Dispose();
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
