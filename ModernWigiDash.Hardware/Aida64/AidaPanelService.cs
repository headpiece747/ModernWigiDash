using ModernWigiDash.Hardware.Service;

namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// The AIDA64 panel master's production wiring: the vendor's named shared map
/// for the frame reads and the vendor WCF service for the small handshake
/// writes (the map's own handle is opened read-only and the vendor's mutex is
/// denied to a non-elevated process).
/// </summary>
public static class AidaPanelService
{
    /// <summary>Creates the production master. The map is opened lazily on the first poll.</summary>
    /// <param name="log">The diagnostic sink; defaults to <see cref="FileLog"/>.</param>
    public static AidaPanelMaster CreateProduction(Action<string>? log = null)
        => new(new MemoryMappedAidaMmapSource(), new VendorAidaMmapWriter(), log);
}
