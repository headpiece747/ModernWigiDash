namespace ModernWigiDash.Sdk;

/// <summary>
/// A single hardware sensor reading collected by LibreHardwareService.
/// </summary>
public sealed record SensorReadingDto
{
    /// <summary>The sensor's identifier as published by LibreHardwareService.</summary>
    public string SensorId { get; set; } = string.Empty;

    /// <summary>The sensor's display name (e.g. "GPU Core Load").</summary>
    public string SensorName { get; set; } = string.Empty;

    /// <summary>The owning hardware component's name (e.g. "NVIDIA GeForce RTX …").</summary>
    public string HardwareName { get; set; } = string.Empty;

    /// <summary>The hardware component type (e.g. "Gpu", "Cpu", "Motherboard").</summary>
    public string HardwareType { get; set; } = string.Empty;

    /// <summary>The sensor type key (e.g. "Load", "Temperature", "Clock").</summary>
    public string SensorType { get; set; } = string.Empty;

    /// <summary>The reading's unit symbol (e.g. "°C", "%").</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>The current reading.</summary>
    public double Value { get; set; }

    /// <summary>The lowest reading observed since the publisher's window started.</summary>
    public double Min { get; set; }

    /// <summary>The highest reading observed since the publisher's window started.</summary>
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
    /// <summary>True when LibreHardwareService is running and the shared-memory
    /// map produced at least one complete snapshot.</summary>
    public bool IsConnected { get; set; }

    /// <summary>The producer's timestamp for this snapshot (preserved from the
    /// map header; the store's freshness window compares against it).</summary>
    public DateTime LastUpdate { get; set; }

    /// <summary>The sensor readings in this snapshot.</summary>
    public IReadOnlyList<SensorReadingDto> Readings { get; set; } = [];
}
