namespace ModernWigiDash.Hardware.Aida64;

/// <summary>
/// The named-map I/O seam behind the AIDA64 panel reader (Hardware): one
/// bounded copy of a byte range from the vendor's shared panel map, or false
/// with a diagnostic error. The real adapter
/// (<see cref="MemoryMappedAidaMmapSource"/>) owns the MemoryMappedFile/Mutex
/// specifics; in-memory fakes drive the reader's validation policy in tests.
/// </summary>
public interface IAidaMmapSource : IDisposable
{
    /// <summary>
    /// Copies <paramref name="length"/> bytes at <paramref name="offset"/> from
    /// the panel map into <paramref name="destination"/>. Returns false with a
    /// diagnostic <paramref name="error"/> when the map is unavailable, the
    /// range is out of the map's bounds, or the copy fails. Never throws.
    /// </summary>
    bool TryRead(int offset, int length, byte[] destination, out string? error);
}
