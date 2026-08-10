using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// In-process cache of the latest hardware sensor snapshot read from the
/// LibreHardwareService shared-memory maps (ADR-0004). The App's polling loop
/// calls <see cref="UpdateFromDto"/>; widgets read the cached snapshot on the
/// render thread. The store owns the staleness decision — consumers ask
/// <see cref="TryReadFresh"/> and cannot skip the check. The snapshot shape is
/// the DTO itself (no shadow record); the reading label derives on the DTO.
/// </summary>
public static class LhmSensorStore
{
    /// <summary>Default staleness window for sensor data (~1s poll cadence).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(10);

    private static readonly StaticTelemetryStore<SensorSnapshotDto> Store = new(
        new SensorSnapshotDto(),
        defaultMaxAge: DefaultMaxAge);

    /// <summary>
    /// Returns the cached snapshot regardless of freshness. Only the inspector's
    /// live sensor picker uses this — it needs the full reading list even when
    /// stale; every other consumer must go through <see cref="TryReadFresh"/>.
    /// </summary>
    public static SensorSnapshotDto ReadSnapshot() => Store.ReadSnapshot();

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// freshness decision uses the producer timestamp with an injectable clock.
    /// </summary>
    public static SensorSnapshotDto? TryReadFresh(TimeSpan? maxAge = null, TimeProvider? timeProvider = null)
        => Store.TryReadFresh(maxAge, timeProvider);

    /// <summary>
    /// Stores a snapshot. A default/empty producer timestamp is resolved to the
    /// store's receive time.
    /// </summary>
    public static void Update(SensorSnapshotDto snapshot) => Store.Update(snapshot, snapshot.LastUpdate);

    /// <summary>
    /// Stores a snapshot from the producer, tolerating a null DTO (treated as
    /// the disconnected state). Keeps the null-tolerant entry point the poll
    /// loop and the tests rely on.
    /// </summary>
    public static void UpdateFromDto(SensorSnapshotDto? dto)
        => Store.Update(dto ?? new SensorSnapshotDto(), dto?.LastUpdate ?? default);

    /// <summary>
    /// Resets the cache to the disconnected state. Intended for test isolation.
    /// </summary>
    public static void Reset() => Store.Reset();
}
