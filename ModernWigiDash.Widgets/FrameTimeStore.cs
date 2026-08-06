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
        new(false, 0, string.Empty, 0, 0, 0, 0, 0, 0, Array.Empty<double>());

    /// <summary>
    /// True when the snapshot was produced by an active polling loop within
    /// <paramref name="maxAge"/>. A stale snapshot means the App stopped
    /// polling (service disconnected or app suspending), so widgets should
    /// render their unavailable state instead of frozen data.
    /// </summary>
    public bool IsFresh(TimeSpan maxAge) => LastUpdate != default && DateTime.UtcNow - LastUpdate <= maxAge;
}

/// <summary>
/// In-process cache of the latest frame-time snapshot fetched from the service
/// over WCF. The App's polling loop calls <see cref="Update"/>; widgets read the
/// cached snapshot on the render thread without touching WCF.
/// </summary>
public static class FrameTimeStore
{
    private static readonly object Gate = new();
    private static FrameTimeSnapshotRecord _current = FrameTimeSnapshotRecord.Unavailable();

    public static FrameTimeSnapshotRecord ReadSnapshot()
    {
        lock (Gate)
        {
            return _current;
        }
    }

    public static void Update(FrameTimeSnapshotRecord snapshot)
    {
        lock (Gate)
        {
            _current = snapshot with { LastUpdate = DateTime.UtcNow };
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
            dto?.RecentFrameTimesMs ?? []));
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
}
