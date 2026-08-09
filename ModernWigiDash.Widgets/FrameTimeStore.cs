using ModernWigiDash.Sdk;

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
    double GpuBusyMs,
    double CpuFrameTimeMs,
    IReadOnlyList<double> RecentFrameTimesMs,
    DateTime LastUpdate = default,
    bool CaptureHealthy = true)
{
    public static FrameTimeSnapshotRecord Unavailable() =>
        new(false, 0, string.Empty, 0, 0, 0, 0, 0, 0, []);
}

/// <summary>
/// In-process cache of the latest frame-time snapshot polled from the
/// PresentMon Service (ADR-0003). The App's polling loop calls
/// <see cref="Update"/>; widgets read the cached snapshot on the render
/// thread. The store owns the staleness decision — consumers ask
/// <see cref="TryReadFresh"/> and cannot skip the check.
/// </summary>
public static class FrameTimeStore
{
    /// <summary>Default staleness window for frame-time data (~1s poll cadence).</summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromSeconds(5);

    private static readonly TelemetryStore<FrameTimeSnapshotRecord> Store = new(
        FrameTimeSnapshotRecord.Unavailable(),
        defaultMaxAge: DefaultMaxAge);

    public static FrameTimeSnapshotRecord ReadSnapshot() => Store.Current ?? FrameTimeSnapshotRecord.Unavailable();

    /// <summary>
    /// Returns the cached snapshot when it is fresh enough, else null. The
    /// freshness decision uses the producer timestamp with an injectable clock.
    /// </summary>
    public static FrameTimeSnapshotRecord? TryReadFresh(TimeSpan? maxAge = null, TimeProvider? timeProvider = null)
        => Store.TryReadFresh(maxAge, timeProvider);

    /// <summary>
    /// Stores a snapshot. A default/empty producer timestamp is resolved to the
    /// store's receive time.
    /// </summary>
    public static void Update(FrameTimeSnapshotRecord snapshot) => Store.Update(snapshot, snapshot.LastUpdate);

    /// <summary>
    /// Maps a PresentMon frame-time DTO into the widget-side record and caches it.
    /// Keeps the DTO-to-render-model mapping owned by the store.
    /// </summary>
    public static void UpdateFromDto(FrameTimeSnapshotDto? dto)
    {
        Store.Update(new FrameTimeSnapshotRecord(
            dto?.IsAvailable ?? false,
            dto?.ProcessId ?? 0,
            dto?.ProcessName ?? string.Empty,
            dto?.Fps ?? 0,
            dto?.FrameTimeMs ?? 0,
            dto?.Low1PercentFps ?? 0,
            dto?.Low01PercentFps ?? 0,
            dto?.GpuBusyMs ?? 0,
            dto?.CpuFrameTimeMs ?? 0,
            dto?.RecentFrameTimesMs ?? [],
            dto?.LastUpdate ?? default,
            dto?.CaptureHealthy ?? true),
            dto?.LastUpdate ?? default);
    }

    /// <summary>
    /// Resets the cache to the unavailable state. Intended for test isolation.
    /// </summary>
    public static void Reset() => Store.Reset();
}
