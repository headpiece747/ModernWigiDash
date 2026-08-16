namespace ModernWigiDash.App.LibreHardwareService;

/// <summary>
/// The named-map I/O seam behind the LHS reader: one mutex-guarded, bounded
/// copy of the sensors map, or null with an error when the map or mutex is
/// unavailable. The real adapter owns the MemoryMappedFile/Mutex specifics;
/// tests substitute an in-memory fake, so the reader's Poll policy (map
/// present, map absent, parse outcome) is drivable without named maps.
/// </summary>
internal interface ILhmMapSource
{
    /// <summary>
    /// Copies the sensors map under the writer's mutex, bounded per the
    /// protocol caps. Null with <paramref name="error"/> when the mutex times
    /// out or the map cannot be opened; the copy itself never throws.
    /// </summary>
    byte[]? TryReadSensorsMap(out string? error);
}
