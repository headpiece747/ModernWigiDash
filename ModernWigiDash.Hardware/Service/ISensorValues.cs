namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The HWiNFO sensor read facet of the vendor WigiDash service (ADR-0022).
/// Stateless reads over the established channel; the client owns the connection.
/// </summary>
public interface ISensorValues
{
    /// <summary>True when the sensor provider is initialized and ready to serve readings.</summary>
    bool IsReady { get; }

    /// <summary>The list of sensors the vendor has registered, or null when unavailable.</summary>
    IReadOnlyList<VendorSensorItem>? GetSensorList();

    /// <summary>
    /// Reads a single sensor value. Returns null when the sensor is unknown or
    /// the read fails (the tolerant-parsing rule: no throw into the render tick).
    /// </summary>
    VendorSensorReading? GetSensorValue(int readingType, int sensorId1, int sensorId2);
}
