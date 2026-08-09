namespace ModernWigiDash.Sdk;

/// <summary>
/// A point-in-time snapshot of the FPS / frame-time telemetry captured via the
/// PresentMon Service (ADR-0003): the App connects non-elevated through
/// <c>pmOpenSession</c>, tracks the foreground presenter, and maps dynamic and
/// frame-query results into this DTO. Frame times are in milliseconds; FPS
/// values are 1000 / frame time.
/// </summary>
public class FrameTimeSnapshotDto
{
    /// <summary>
    /// Whether PresentMon frame capture is active. False when the PresentMon
    /// Service is not installed/running or the API library is unavailable.
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>
    /// Whether the capture pipeline is producing present data. False when the
    /// service session is open but no process yields present events for a grace
    /// period (service-side ETW capture dead) — distinct from
    /// <see cref="IsAvailable"/>, which only says the service is reachable.
    /// </summary>
    public bool CaptureHealthy { get; set; } = true;

    /// <summary>
    /// Human-readable reason when <see cref="IsAvailable"/> is false.
    /// </summary>
    public string ErrorMessage { get; set; } = string.Empty;

    public DateTime LastUpdate { get; set; }

    /// <summary>
    /// Process id of the tracked presenter (most active process in the window).
    /// </summary>
    public int ProcessId { get; set; }

    /// <summary>
    /// Process name (e.g. "game.exe") of the tracked presenter.
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// Live frames per second over the rolling measurement window.
    /// </summary>
    public double Fps { get; set; }

    /// <summary>
    /// Current frame time in milliseconds.
    /// </summary>
    public double FrameTimeMs { get; set; }

    /// <summary>
    /// 1% low as FPS (1000 / 99th percentile frame time).
    /// </summary>
    public double Low1PercentFps { get; set; }

    /// <summary>
    /// 0.1% low as FPS (1000 / 99.9th percentile frame time).
    /// </summary>
    public double Low01PercentFps { get; set; }

    /// <summary>
    /// Average GPU busy time per frame for this process's work, in milliseconds
    /// (PM_METRIC_GPU_BUSY, "Ms GPU Busy").
    /// </summary>
    public double GpuBusyMs { get; set; }

    /// <summary>
    /// Average CPU-side present call duration in milliseconds.
    /// </summary>
    public double CpuFrameTimeMs { get; set; }

    /// <summary>
    /// Recent frame times (ms), newest last, downsampled for a sparkline.
    /// </summary>
    public List<double> RecentFrameTimesMs { get; set; } = [];
}
