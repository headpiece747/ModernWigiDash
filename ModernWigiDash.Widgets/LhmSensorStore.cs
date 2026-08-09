using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// A single hardware sensor reading read from LibreHardwareService's
/// shared-memory maps (ADR-0004). Identity: <see cref="SensorId"/> is the
/// stable machine key (matches <c>SensorReadingDto.SensorId</c>);
/// <see cref="Label"/> is the human-facing "<c>HardwareName: SensorName</c>"
/// string used by the widget picker. Lookups may match on either, but
/// SensorId is the canonical key.
/// </summary>
public sealed record LhmReading(
    string SensorId,
    string SensorName,
    string Label,
    string Unit,
    double Value,
    double Min,
    double Max,
    double Avg);

/// <summary>
/// A point-in-time snapshot of the live hardware sensor set.
/// </summary>
public sealed record LhmSnapshot(bool IsConnected, DateTime LastUpdate, IReadOnlyList<LhmReading> Readings)
{
    public static LhmSnapshot Disconnected() => new(false, DateTime.MinValue, []);
}

/// <summary>
/// In-process cache of the latest hardware sensor snapshot read from the
/// LibreHardwareService shared-memory maps (ADR-0004). The App's polling loop
/// calls <see cref="Update"/>; widgets read the cached snapshot on the render
/// thread. The store owns the staleness decision — consumers ask
/// <see cref="TryReadFresh"/> and cannot skip the check.
/// </summary>
public static class LhmSensorStore
{
    /// <summary>Default staleness window for sensor data (~1s poll cadence).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(10);

    private static readonly TelemetryStore<LhmSnapshot> Store = new(
        LhmSnapshot.Disconnected(),
        defaultMaxAge: DefaultMaxAge);

    public static LhmSnapshot ReadSnapshot() => Store.Current;

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// freshness decision uses the producer timestamp with an injectable clock.
    /// </summary>
    public static LhmSnapshot? TryReadFresh(TimeSpan? maxAge = null, TimeProvider? timeProvider = null)
        => Store.TryReadFresh(maxAge, timeProvider);

    /// <summary>
    /// Stores a snapshot. A default/empty producer timestamp is resolved to the
    /// store's receive time.
    /// </summary>
    public static void Update(LhmSnapshot snapshot) => Store.Update(snapshot, snapshot.LastUpdate);

    /// <summary>
    /// Maps a LibreHardwareService sensor snapshot DTO into the widget-side
    /// snapshot and caches it. Keeps the DTO-to-render-model mapping owned by
    /// the store.
    /// </summary>
    public static void UpdateFromDto(SensorSnapshotDto? dto)
    {
        var readings = dto?.Readings
            .Select(r => new LhmReading(
                r.SensorId,
                r.SensorName,
                $"{r.HardwareName}: {r.SensorName}",
                r.Unit,
                r.Value,
                r.Min,
                r.Max,
                r.Avg))
            .ToList() ?? [];

        Store.Update(new LhmSnapshot(
            dto?.IsConnected ?? false,
            dto?.LastUpdate ?? default,
            readings),
            dto?.LastUpdate ?? default);
    }

    /// <summary>
    /// Resets the cache to the disconnected state. Intended for test isolation.
    /// </summary>
    public static void Reset() => Store.Reset();
}
