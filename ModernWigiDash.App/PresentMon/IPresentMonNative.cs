namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// A point-in-time dynamic-query poll for one tracked process. Values are the
/// raw PresentMon API results — units match the API's metric table (verified
/// via the service's runtime introspection): GPU busy is milliseconds per
/// frame (PM_METRIC_GPU_BUSY, "Ms GPU Busy"); the producer converts it to the
/// overlay-style busy-per-frame percentage.
/// </summary>
public sealed record PresentMonDynamicSample(
    double Fps,
    double Low1PercentFps,
    double GpuBusyMs,
    double CpuFrameTimeMs,
    double DisplayedFps,
    int DroppedFrames,
    double GpuTimeMs,
    int PresentModeId);

/// <summary>
/// One dynamic-query poll's outcome: the sample (null when the process has no
/// data yet or the poll failed) plus the raw PresentMon status, so callers can
/// tell a benign "no data yet" poll from a dead session
/// (<see cref="PmStatus.SessionNotOpen"/>, <see cref="PmStatus.PipeError"/>,
/// <see cref="PmStatus.ServiceError"/>).
/// </summary>
public sealed record PresentMonPollResult(PresentMonDynamicSample? Sample, PmStatus Status);

/// <summary>
/// The seam between the PresentMon producer and the runtime-loaded
/// <c>PresentMonAPI2.dll</c>. PresentMon is not installed in every dev
/// environment, so every native call lives behind this interface and the real
/// implementation loads the DLL at runtime from the PresentMon SDK install
/// directory. Tests inject a fake.
/// </summary>
public interface IPresentMonNative : IDisposable
{
    /// <summary>True when the native API library can be loaded.</summary>
    bool IsAvailable { get; }

    /// <summary>Human-readable reason when <see cref="IsAvailable"/> is false
    /// or a session/track operation fails (e.g. "PresentMonAPI2.dll not found").</summary>
    string? UnavailableReason { get; }

    /// <summary>Opens a session to the PresentMon Service. Returns false when
    /// the service is unreachable.</summary>
    bool OpenSession();

    /// <summary>Closes the session and releases query handles.</summary>
    void CloseSession();

    /// <summary>Commands the service to track a process id. Returns true on
    /// success or when the process is already being tracked.</summary>
    bool TrackProcess(int processId);

    /// <summary>Polls the dynamic query for the process, returning the first
    /// swap chain's stats plus the service status. Sample is null when the
    /// process has no data yet (Success status) or when the poll failed.</summary>
    PresentMonPollResult PollDynamic(int processId);

    /// <summary>Drains and consumes pending per-frame frame times (ms) for the
    /// process from the frame-query queue.</summary>
    IReadOnlyList<double> DrainFrameTimes(int processId);
}
