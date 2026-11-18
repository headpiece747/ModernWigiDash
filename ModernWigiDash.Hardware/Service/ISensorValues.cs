namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// Connection and catalog readiness states reported by the vendor HWiNFO facet.
/// </summary>
public enum VendorSensorStatus
{
    /// <summary>The vendor service channel is not open or disconnected.</summary>
    Disconnected,

    /// <summary>The vendor service channel is open, but the shared HWiNFO provider is not initialized.</summary>
    ProviderNotInitialized,

    /// <summary>The provider is initialized, but the vendor service reported zero HWiNFO sensors.</summary>
    EmptySensorList,

    /// <summary>The provider is initialized and serving a non-empty sensor catalog.</summary>
    Ready,
}

/// <summary>
/// The HWiNFO sensor read facet of the vendor WigiDash service (ADR-0022).
/// Stateless reads over the established channel; the client owns the connection.
/// </summary>
public interface ISensorValues
{
    /// <summary>True when the sensor provider is initialized and ready to serve readings.</summary>
    bool IsReady { get; }

    /// <summary>Detailed connection/catalog status for user diagnostics and cache invalidation.</summary>
    VendorSensorStatus Status => IsReady ? VendorSensorStatus.Ready : VendorSensorStatus.Disconnected;

    /// <summary>The list of sensors the vendor has registered, or null when unavailable.</summary>
    IReadOnlyList<VendorSensorItem>? GetSensorList();

    /// <summary>
    /// Reads a single sensor value. Returns null when the sensor is unknown or
    /// the read fails (the tolerant-parsing rule: no throw into the render tick).
    /// </summary>
    VendorSensorReading? GetSensorValue(int readingType, int sensorId1, int sensorId2);
}
