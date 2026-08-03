using System.Runtime.Serialization;

namespace ModernWigiDash.Service.Wcf;

/// <summary>
/// A single hardware sensor reading collected by LibreHardwareMonitorLib.
/// </summary>
[DataContract(Namespace = "http://modernwigidash.service/2024")]
public class SensorReadingDto
{
    [DataMember]
    public string SensorId { get; set; } = string.Empty;

    [DataMember]
    public string SensorName { get; set; } = string.Empty;

    [DataMember]
    public string HardwareName { get; set; } = string.Empty;

    [DataMember]
    public string HardwareType { get; set; } = string.Empty;

    [DataMember]
    public string SensorType { get; set; } = string.Empty;

    [DataMember]
    public string Unit { get; set; } = string.Empty;

    [DataMember]
    public double Value { get; set; }

    [DataMember]
    public double Min { get; set; }

    [DataMember]
    public double Max { get; set; }

    [DataMember]
    public double Avg { get; set; }
}

/// <summary>
/// A point-in-time snapshot of the hardware sensor set polled by the service.
/// </summary>
[DataContract(Namespace = "http://modernwigidash.service/2024")]
public class SensorSnapshotDto
{
    [DataMember]
    public bool IsConnected { get; set; }

    [DataMember]
    public DateTime LastUpdate { get; set; }

    [DataMember]
    public List<SensorReadingDto> Readings { get; set; } = [];
}
