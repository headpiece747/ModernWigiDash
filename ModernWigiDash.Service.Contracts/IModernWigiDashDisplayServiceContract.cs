using System.Runtime.Serialization;
using ModernWigiDash.Service.Contracts;

namespace ModernWigiDash.Service.Wcf;

/// <summary>
/// CoreWCF service contract for ModernWigiDash display service.
/// Dual-decorated with both CoreWCF and System.ServiceModel attributes so that:
/// - CoreWCF server recognizes [CoreWCF.ServiceContract] for host setup
/// - System.ServiceModel.ChannelFactory recognizes [System.ServiceModel.ServiceContract] for client proxies
/// </summary>
[CoreWCF.ServiceContract(Namespace = "http://modernwigidash.service/2024")]
[System.ServiceModel.ServiceContract(Namespace = "http://modernwigidash.service/2024")]
public interface IModernWigiDashDisplayServiceContract
{
    /// <summary>
    /// Initialize the display hardware connection.
    /// </summary>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    bool InitializeDisplay();

    /// <summary>
    /// Deinitialize the display hardware connection.
    /// </summary>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    bool DeInitializeDisplay();

    /// <summary>
    /// Get the current connection status of the display device.
    /// </summary>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    DisplayStatus GetDisplayStatus();

    /// <summary>
    /// Set the display brightness level (0-100).
    /// </summary>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    bool SetBrightness(byte brightnessPercent);

    /// <summary>
    /// Queues a frame buffer payload for delivery to the display device.
    /// Returns true when the frame was accepted into the delivery queue; under
    /// the DropOldest policy a later frame may supersede it before the worker
    /// writes it to the device.
    /// </summary>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    bool SendFrame(FramePayload payload);

    /// <summary>
    /// Get diagnostic information about the service and device.
    /// </summary>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    ServiceDiagnostics GetDiagnostics();

    /// <summary>
    /// Get the CoreWCF service version string.
    /// </summary>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    string GetVersion();

    /// <summary>
    /// Poll for a touch event from the display. Returns null if no touch since last poll.
    /// </summary>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    TouchEventInfo? PollTouch();

    /// <summary>
    /// Reset display to standby: clear framebuffer and switch to the welcome screen.
    /// Called when the app closes so the display doesn't stay black.
    /// </summary>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    bool Shutdown();

    /// <summary>
    /// Get the latest hardware sensor snapshot collected by the LibreHardwareMonitor
    /// background worker running in the LocalSystem service context.
    /// </summary>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    SensorSnapshotDto GetSensorSnapshot();

    /// <summary>
    /// Get the latest FPS / frame-time snapshot captured by the service's
    /// in-process ETW frame reader (Microsoft-Windows-DXGI / D3D9 / DxgKrnl).
    /// </summary>
    /// <param name="preferredProcessId">
    /// When &gt; 0, the snapshot targets that process if it has recent presents.
    /// When -1, no process is targeted (the caller wants the idle/monitor view).
    /// When 0, the most active presenting process is selected.
    /// </param>
    [CoreWCF.OperationContract]
    [System.ServiceModel.OperationContract]
    FrameTimeSnapshotDto GetFrameTimeSnapshot(int preferredProcessId = 0);
}

/// <summary>
/// Data contract for touch input events relayed from the display hardware.
/// </summary>
[DataContract(Namespace = "http://modernwigidash.service/2024")]
public class TouchEventInfo
{
    [DataMember]
    public byte Type { get; set; }

    [DataMember]
    public short X { get; set; }

    [DataMember]
    public short Y { get; set; }

    [DataMember]
    public long TimestampUtcTicks { get; set; }
}

/// <summary>
/// Data contract for display connection status.
/// </summary>
[DataContract(Namespace = "http://modernwigidash.service/2024")]
public class DisplayStatus
{
    [DataMember]
    public bool IsConnected { get; set; }

    [DataMember]
    public string DevicePath { get; set; } = string.Empty;

    [DataMember]
    public string State { get; set; } = string.Empty;

    [DataMember]
    public string DiagnosticSummary { get; set; } = string.Empty;

    [DataMember]
    public long TotalFramesProcessed { get; set; }
}

/// <summary>
/// Data contract for service diagnostics.
/// </summary>
[DataContract(Namespace = "http://modernwigidash.service/2024")]
public class ServiceDiagnostics
{
    [DataMember]
    public string ServiceName { get; set; } = string.Empty;

    [DataMember]
    public string ServiceAccount { get; set; } = string.Empty;

    [DataMember]
    public string Uptime { get; set; } = string.Empty;

    [DataMember]
    public string DisplayStatus { get; set; } = string.Empty;

    [DataMember]
    public string WcfEndpoint { get; set; } = string.Empty;

    [DataMember]
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// Data contract for frame buffer payloads.
/// </summary>
[DataContract(Namespace = "http://modernwigidash.service/2024")]
public class FramePayload
{
    [DataMember]
    public byte[] Data { get; set; } = [];
}
