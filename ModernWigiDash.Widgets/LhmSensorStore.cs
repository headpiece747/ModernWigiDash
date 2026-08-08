using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// A single hardware sensor reading collected by the service via LibreHardwareMonitor.
/// Identity: <see cref="SensorId"/> is the stable machine key (matches
/// <c>SensorReadingDto.SensorId</c>); <see cref="Label"/> is the human-facing
/// "<c>HardwareName: SensorName</c>" string used by the widget picker. Lookups
/// may match on either, but SensorId is the canonical key.
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

    /// <summary>
    /// True when the snapshot was produced by an active polling loop within
    /// <paramref name="maxAge"/> — measured against the producer timestamp, so
    /// cross-machine clock skew does not affect the decision. A stale snapshot
    /// means the App stopped polling (service disconnected or app suspending),
    /// so widgets should render their unavailable state instead of frozen data.
    /// </summary>
    public bool IsFresh(TimeSpan maxAge, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        return LastUpdate != DateTime.MinValue && now - LastUpdate <= maxAge;
    }
}

/// <summary>
/// In-process cache of the latest hardware sensor snapshot fetched from the
/// service over WCF. The App's polling loop calls <see cref="Update"/>; widgets
/// read the cached snapshot on the render thread without touching WCF. The
/// store owns the staleness decision — consumers ask <see cref="TryReadFresh"/>
/// and cannot skip the check.
/// </summary>
public static class LhmSensorStore
{
    /// <summary>Default staleness window for sensor data (~1s poll cadence).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(10);

    private static readonly Lock Gate = new();
    private static LhmSnapshot _current = LhmSnapshot.Disconnected();

    public static LhmSnapshot ReadSnapshot()
    {
        lock (Gate)
        {
            return _current;
        }
    }

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// freshness decision uses the producer timestamp with an injectable clock.
    /// </summary>
    public static LhmSnapshot? TryReadFresh(TimeSpan? maxAge = null, TimeProvider? timeProvider = null)
    {
        var snapshot = ReadSnapshot();
        return snapshot.IsFresh(maxAge ?? DefaultMaxAge, timeProvider) ? snapshot : null;
    }

    /// <summary>
    /// Stores a snapshot. The producer timestamp is preserved — <see cref="UpdateFromDto"/>
    /// is responsible for providing it (falling back to the receive time when
    /// the producer did not stamp one).
    /// </summary>
    public static void Update(LhmSnapshot snapshot)
    {
        lock (Gate)
        {
            _current = snapshot;
        }
    }

    /// <summary>
    /// Maps a service sensor snapshot DTO into the widget-side snapshot and
    /// caches it. Keeps the DTO-to-render-model mapping owned by the store.
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

        Update(new LhmSnapshot(
            dto?.IsConnected ?? false,
            ProducerTimestamp(dto?.LastUpdate),
            readings));
    }

    /// <summary>
    /// Resets the cache to the disconnected state. Intended for test isolation.
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _current = LhmSnapshot.Disconnected();
        }
    }

    private static DateTime ProducerTimestamp(DateTime? producer)
        => producer is { } ts && ts != default ? ts : TimeProvider.System.GetUtcNow().UtcDateTime;
}
