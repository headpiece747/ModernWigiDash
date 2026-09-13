namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The vendor WCF service's sensor item shape (the `SensorItem` data contract
/// from `IWigiDashWcf.GetSensorList`). Mirrors the vendor's field names exactly
/// so a future contract change is visible at compile time.
/// </summary>
public sealed record VendorSensorItem(
    Guid Guid,
    string Name,
    int ReadingType,
    int SensorId1,
    int SensorId2,
    string? Type,
    string? Unit);

/// <summary>
/// A single sensor reading from the vendor service. `IsValid` is false when the
/// vendor could not produce a value for this sensor at read time.
/// </summary>
public sealed record VendorSensorReading(double Value, bool IsValid);
