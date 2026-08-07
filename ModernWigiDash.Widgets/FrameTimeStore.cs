using ModernWigiDash.Service.Contracts;

namespace ModernWigiDash.Widgets;

/// <summary>
/// A point-in-time snapshot of the live FPS / frame-time telemetry captured by
/// the service via in-process ETW (DXGI / D3D9 / DxgKrnl present events).
/// Frame times are in milliseconds; FPS values are 1000 / frame time.
/// </summary>
public sealed record FrameTimeSnapshotRecord(
    bool IsAvailable,
    int ProcessId,
    string ProcessName,
    double Fps,
    double FrameTimeMs,
    double Low1PercentFps,
    double Low01PercentFps,
    double GpuBusyPercent,
    double CpuFrameTimeMs,
    IReadOnlyList<double> RecentFrameTimesMs,
    DateTime LastUpdate = default)
{
    public static FrameTimeSnapshotRecord Unavailable() =>
        new(false, 0, string.Empty, 0, 0, 0, 0, 0, 0, []);

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
        return LastUpdate != default && now - LastUpdate <= maxAge;
    }
}

/// <summary>
/// In-process cache of the latest frame-time snapshot fetched from the service
/// over WCF. The App's polling loop calls <see cref="Update"/>; widgets read the
/// cached snapshot on the render thread without touching WCF. The store owns
/// the staleness decision — consumers ask <see cref="TryReadFresh"/> and cannot
/// skip the check.
/// </summary>
public static class FrameTimeStore
{
    /// <summary>Default staleness window for frame-time data (~1s poll cadence).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(5);

    private static readonly Lock Gate = new();
    private static FrameTimeSnapshotRecord _current = FrameTimeSnapshotRecord.Unavailable();

    public static FrameTimeSnapshotRecord ReadSnapshot()
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
    public static FrameTimeSnapshotRecord? TryReadFresh(TimeSpan? maxAge = null, TimeProvider? timeProvider = null)
    {
        var snapshot = ReadSnapshot();
        return snapshot.IsFresh(maxAge ?? DefaultMaxAge, timeProvider) ? snapshot : null;
    }

    /// <summary>
    /// Stores a snapshot. The producer timestamp is preserved — <see cref="UpdateFromDto"/>
    /// is responsible for providing it (falling back to the receive time when
    /// the producer did not stamp one).
    /// </summary>
    public static void Update(FrameTimeSnapshotRecord snapshot)
    {
        lock (Gate)
        {
            _current = snapshot;
        }
    }

    /// <summary>
    /// Maps a service frame-time DTO into the widget-side record and caches it.
    /// Keeps the DTO-to-render-model mapping owned by the store.
    /// </summary>
    public static void UpdateFromDto(FrameTimeSnapshotDto? dto)
    {
        Update(new FrameTimeSnapshotRecord(
            dto?.IsAvailable ?? false,
            dto?.ProcessId ?? 0,
            dto?.ProcessName ?? string.Empty,
            dto?.Fps ?? 0,
            dto?.FrameTimeMs ?? 0,
            dto?.Low1PercentFps ?? 0,
            dto?.Low01PercentFps ?? 0,
            dto?.GpuBusyPercent ?? 0,
            dto?.CpuFrameTimeMs ?? 0,
            dto?.RecentFrameTimesMs ?? [],
            ProducerTimestamp(dto?.LastUpdate)));
    }

    /// <summary>
    /// Resets the cache to the unavailable state. Intended for test isolation.
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _current = FrameTimeSnapshotRecord.Unavailable();
        }
    }

    private static DateTime ProducerTimestamp(DateTime? producer)
        => producer is { } ts && ts != default ? ts : TimeProvider.System.GetUtcNow().UtcDateTime;
}
