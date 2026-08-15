using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// In-process cache of the latest hardware sensor snapshot read from the
/// LibreHardwareService shared-memory maps (ADR-0004). The App's polling loop
/// calls <see cref="UpdateFromDto"/>; widgets read the cached snapshot on the
/// render thread. The store owns the staleness decision — consumers ask
/// <see cref="TryReadFresh"/> and cannot skip the check. The snapshot shape is
/// the DTO itself (no shadow record); the reading label derives on the DTO.
/// One instance of the shared <see cref="TelemetryStoreFacade{TRecord}"/>.
/// </summary>
public static class LhmSensorStore
{
    private static readonly TelemetryStoreFacade<SensorSnapshotDto> Facade = new(
        new SensorSnapshotDto(),
        defaultMaxAge: TimeSpan.FromSeconds(10),
        lastUpdateOf: dto => dto.LastUpdate);

    /// <summary>Default staleness window for sensor data (~1s poll cadence).</summary>
    public static TimeSpan DefaultMaxAge => Facade.DefaultMaxAge;

    /// <summary>
    /// Internal test seam: builds a store bound to a fake clock (and optional
    /// max age) so the facade freshness tests can drive time. The production
    /// singleton binds <see cref="TimeProvider.System"/> at construction.
    /// </summary>
    internal static TelemetryStore<SensorSnapshotDto> CreateStoreForTest(TimeProvider timeProvider, TimeSpan? maxAge = null)
        => Facade.CreateStoreForTest(timeProvider, maxAge);

    /// <summary>Internal test seam: installs the store behind the static
    /// read/update surface (see <see cref="CreateStoreForTest"/>).</summary>
    internal static TelemetryStore<SensorSnapshotDto> StoreForTest
    {
        get => Facade.StoreForTest;
        set => Facade.StoreForTest = value;
    }

    /// <summary>
    /// Returns the cached snapshot regardless of freshness. Only the inspector's
    /// live sensor picker uses this — it needs the full reading list even when
    /// stale; every other consumer must go through <see cref="TryReadFresh"/>.
    /// </summary>
    public static SensorSnapshotDto ReadSnapshot() => Facade.ReadSnapshot();

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// staleness window and the clock bind at construction.
    /// </summary>
    public static SensorSnapshotDto? TryReadFresh() => Facade.TryReadFresh();

    /// <summary>
    /// Stores a snapshot from the producer, tolerating a null DTO (treated as
    /// the disconnected state). The single write entry point.
    /// </summary>
    public static void UpdateFromDto(SensorSnapshotDto? dto) => Facade.UpdateFromDto(dto);

    /// <summary>
    /// Resets the cache to the disconnected state. Intended for test isolation.
    /// </summary>
    public static void Reset() => Facade.Reset();
}
