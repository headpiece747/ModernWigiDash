namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// The AIDA64 panel master's write half: initializes the vendor's map provider
/// and writes the small handshake bytes (header, widget record, heartbeat
/// counter, frame ack). The production adapter goes through the vendor WCF
/// service, whose map handle carries the vendor's mutex and whose request quota
/// caps writes at ~64 KB; frame pixels are never written through here.
/// </summary>
public interface IAidaMmapWriter
{
    /// <summary>Ensures the vendor's AIDA64 map provider is initialized; false with a reason when unavailable.</summary>
    bool TryInit(out string? error);

    /// <summary>Writes <paramref name="data"/> at <paramref name="offset"/> in the vendor's shared map.</summary>
    bool TryWrite(int offset, byte[] data, out string? error);
}
