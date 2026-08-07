using ModernWigiDash.Service.Contracts;

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
    public static LhmSnapshot Disconnected() => new(false, DateTime.MinValue, Array.Empty<LhmReading>());

    /// <summary>
    /// True when the snapshot was produced by an active polling loop within
    /// <paramref name="maxAge"/>. A stale snapshot means the App stopped
    /// polling (service disconnected or app suspending), so widgets should
    /// render their unavailable state instead of frozen data.
    /// </summary>
    public bool IsFresh(TimeSpan maxAge) => LastUpdate != DateTime.MinValue && DateTime.UtcNow - LastUpdate <= maxAge;
}

/// <summary>
/// In-process cache of the latest hardware sensor snapshot fetched from the
/// service over WCF. The App's polling loop calls <see cref="Update"/>; widgets
/// read the cached snapshot on the render thread without touching WCF.
/// </summary>
public static class LhmSensorStore
{
    private static readonly Lock Gate = new();
    private static LhmSnapshot _current = LhmSnapshot.Disconnected();

    public static LhmSnapshot ReadSnapshot()
    {
        lock (Gate)
        {
            return _current;
        }
    }

    public static void Update(LhmSnapshot snapshot)
    {
        lock (Gate)
        {
            _current = snapshot with { LastUpdate = DateTime.UtcNow };
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
            dto?.LastUpdate ?? DateTime.UtcNow,
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
}
