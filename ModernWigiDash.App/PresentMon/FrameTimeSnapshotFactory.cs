using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// Shapes the four snapshot outcomes of the frame-time producer — the only
/// place in the pipeline that builds a <see cref="FrameTimeSnapshotDto"/>. All
/// unit conversions (presented fps → frame time, GPU busy ms → busy-per-frame
/// percent) and the percentile-derived 0.1% low concentrate here, so the
/// display semantics are testable directly instead of through a 100-line poll
/// method.
/// </summary>
public static class FrameTimeSnapshotFactory
{
    /// <summary>Service absent or unreachable: the widget renders the unavailable state.</summary>
    public static FrameTimeSnapshotDto Unavailable(string? reason, DateTime now) => new()
    {
        IsAvailable = false,
        ErrorMessage = reason ?? string.Empty,
        LastUpdate = now,
    };

    /// <summary>No target process this poll: available, healthy, no process.</summary>
    public static FrameTimeSnapshotDto Idle(DateTime now) => new()
    {
        IsAvailable = true,
        CaptureHealthy = true,
        ProcessId = -1,
        LastUpdate = now,
    };

    /// <summary>
    /// Session is up, a target exists, but no present data has arrived for the
    /// whole grace window — the service's ETW capture is not producing events.
    /// The DTO stays "available" (the service is reachable) but flags the
    /// capture unhealthy so the widget can say so instead of showing
    /// fabricated values as real FPS.
    /// </summary>
    public static FrameTimeSnapshotDto CaptureDead(DateTime now) => new()
    {
        IsAvailable = true,
        CaptureHealthy = false,
        ErrorMessage = "PresentMon capture is not producing present data (service ETW capture inactive).",
        ProcessId = -1,
        LastUpdate = now,
    };

    /// <summary>
    /// A live present sample for the tracked process. Frame time derives from
    /// the presented FPS; GPU busy is converted from the raw ms-per-frame
    /// metric (PM_METRIC_GPU_BUSY) to the overlay-style busy-per-frame percent;
    /// the 0.1% low derives from the buffered frame times.
    /// </summary>
    public static FrameTimeSnapshotDto Live(
        int processId,
        string processName,
        PresentMonDynamicSample sample,
        IReadOnlyList<double> recentFrameTimesMs,
        DateTime now) => new()
        {
            IsAvailable = true,
            CaptureHealthy = true,
            LastUpdate = now,
            ProcessId = processId,
            ProcessName = processName,
            Fps = sample.Fps,
            FrameTimeMs = sample.Fps > 0 ? 1000.0 / sample.Fps : 0,
            Low1PercentFps = sample.Low1PercentFps,
            Low01PercentFps = FrameTimeStatistics.Low01PercentFps(recentFrameTimesMs),
            GpuBusyPercent = sample.Fps > 0 ? sample.GpuBusyMs * sample.Fps / 10.0 : 0,
            CpuFrameTimeMs = sample.CpuFrameTimeMs,
            DisplayedFps = sample.DisplayedFps,
            DroppedFrames = sample.DroppedFrames,
            GpuTimeMs = sample.GpuTimeMs,
            PresentModeId = sample.PresentModeId,
            RecentFrameTimesMs = new List<double>(recentFrameTimesMs),
        };
}
