namespace ModernWigiDash.Sdk;

/// <summary>
/// Capability facet on the host context exposing live telemetry snapshot reads (sensors,
/// frame times) to widgets, eliminating mandatory static store access (ADR-0011 exit plan).
/// </summary>
public interface IWidgetTelemetryContext
{
    /// <summary>Reads the current fresh hardware sensor snapshot, or null if stale/disconnected.</summary>
    SensorSnapshotDto? TryReadSensors();

    /// <summary>Reads the current fresh frame-time snapshot, or null if stale/disconnected.</summary>
    FrameTimeSnapshotDto? TryReadFrameTime();
}
