namespace ModernWigiDash.Sdk;

/// <summary>
/// A single hardware sensor reading collected by LibreHardwareService.
/// </summary>
public sealed record SensorReadingDto
{
    public string SensorId { get; set; } = string.Empty;

    public string SensorName { get; set; } = string.Empty;

    public string HardwareName { get; set; } = string.Empty;

    public string HardwareType { get; set; } = string.Empty;

    public string SensorType { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public double Value { get; set; }

    public double Min { get; set; }

    public double Max { get; set; }

    /// <summary>
    /// The human-facing "<c>HardwareName: SensorName</c>" string — the single
    /// derivation site (the sensor picker and the widgets both key on it).
    /// </summary>
    public string Label => $"{HardwareName}: {SensorName}";
}

/// <summary>
/// A point-in-time snapshot of the hardware sensor set read from
/// LibreHardwareService's shared-memory maps.
/// </summary>
public sealed record SensorSnapshotDto
{
    public bool IsConnected { get; set; }

    public DateTime LastUpdate { get; set; }

    public List<SensorReadingDto> Readings { get; set; } = [];
}
