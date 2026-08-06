using System.Runtime.Serialization;

namespace ModernWigiDash.Service.Contracts;

/// <summary>
/// A point-in-time snapshot of the FPS / frame-time telemetry captured by the
/// service via in-process ETW (Microsoft-Windows-DXGI / D3D9 / DxgKrnl).
/// Frame times are in milliseconds; FPS values are 1000 / frame time.
/// </summary>
[DataContract(Namespace = "http://modernwigidash.service/2024")]
public class FrameTimeSnapshotDto
{
    /// <summary>
    /// Whether ETW frame capture is active. False when the service lacks the
    /// admin/SYSTEM privileges required to open a real-time ETW session.
    /// </summary>
    [DataMember]
    public bool IsAvailable { get; set; }

    /// <summary>
    /// Human-readable reason when <see cref="IsAvailable"/> is false.
    /// </summary>
    [DataMember]
    public string ErrorMessage { get; set; } = string.Empty;

    [DataMember]
    public DateTime LastUpdate { get; set; }

    /// <summary>
    /// Process id of the tracked presenter (most active process in the window).
    /// </summary>
    [DataMember]
    public int ProcessId { get; set; }

    /// <summary>
    /// Process name (e.g. "game.exe") of the tracked presenter.
    /// </summary>
    [DataMember]
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// Live frames per second over the rolling measurement window.
    /// </summary>
    [DataMember]
    public double Fps { get; set; }

    /// <summary>
    /// Current frame time in milliseconds.
    /// </summary>
    [DataMember]
    public double FrameTimeMs { get; set; }

    /// <summary>
    /// 1% low as FPS (1000 / 99th percentile frame time).
    /// </summary>
    [DataMember]
    public double Low1PercentFps { get; set; }

    /// <summary>
    /// 0.1% low as FPS (1000 / 99.9th percentile frame time).
    /// </summary>
    [DataMember]
    public double Low01PercentFps { get; set; }

    /// <summary>
    /// Percent of the window the GPU was busy with this process's work.
    /// </summary>
    [DataMember]
    public double GpuBusyPercent { get; set; }

    /// <summary>
    /// Average CPU-side present call duration in milliseconds.
    /// </summary>
    [DataMember]
    public double CpuFrameTimeMs { get; set; }

    /// <summary>
    /// Recent frame times (ms), newest last, downsampled for a sparkline.
    /// </summary>
    [DataMember]
    public List<double> RecentFrameTimesMs { get; set; } = [];
}
