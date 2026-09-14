using System.Runtime.Serialization;

namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The vendor WCF service's sensor item shape (the <c>SensorItem</c> data contract
/// from <c>IWigiDashWcf.GetSensorList</c>). The DataContract name, namespace, and
/// member set mirror the vendor's contract exactly: the WCF serializer matches a
/// data contract by its NAME and NAMESPACE, so a type named
/// <c>VendorSensorItem</c> in our own namespace silently deserialized every
/// response to an EMPTY list against the vendor's <c>SensorItem</c> contract
/// (observed on-device 2026-09-13: the wire carried 674 items, the mirror saw 0,
/// and the HWiNFO widget fell back to its placeholder forever).
/// </summary>
[DataContract(Name = "SensorItem", Namespace = "http://schemas.datacontract.org/2004/07/WigiDashWcf")]
public sealed record VendorSensorItem(
    [property: DataMember] Guid Guid,
    [property: DataMember] string Name,
    [property: DataMember] int ReadingType,
    [property: DataMember] int SensorId1,
    [property: DataMember] int SensorId2,
    [property: DataMember] string? Type,
    [property: DataMember] string? Unit);

/// <summary>
/// A single sensor reading from the vendor service. `IsValid` is false when the
/// vendor could not produce a value for this sensor at read time.
/// </summary>
public sealed record VendorSensorReading(double Value, bool IsValid);
