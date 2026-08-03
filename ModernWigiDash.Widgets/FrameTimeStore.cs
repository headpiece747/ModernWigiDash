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
    IReadOnlyList<double> RecentFrameTimesMs)
{
    public static FrameTimeSnapshotRecord Unavailable() =>
        new(false, 0, string.Empty, 0, 0, 0, 0, 0, 0, Array.Empty<double>());
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
            _current = snapshot;
        }
    }
}
