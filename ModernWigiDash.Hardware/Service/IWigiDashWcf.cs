using System.Collections.Generic;
using System.ServiceModel;

namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The vendor's WCF service contract, mirrored from the decompiled vendor service
/// (WigiDashWcf.dll, ElmorLabs v1.0.0.2) and verified against the live XSD at
/// http://localhost:8733/WigiDashService/WigiDashWcf/?xsd=xsd0 (pulled 2026-09-13).
/// This is a consumption-only mirror: we never host this endpoint, we only call
/// into it. The signatures use <c>out</c> parameters (not tuples) to match the
/// vendor exactly: WCF serializes an <c>out</c> parameter as a separate element
/// in the response, whereas a C# tuple would serialize as a single composite
/// object, which is what broke deserialization on-device (the "Encountered
/// 'Text'" fault at position 147). Field names and method signatures match the
/// vendor's data contracts exactly so a future contract change is visible at
/// compile time. Two wire details are also load-bearing: the sensor payload's
/// data-contract name/namespace (see <see cref="VendorSensorItem"/>) and each
/// operation parameter's element name (see <see cref="GetSensorValue"/>) — WCF
/// matches by name, not position.
/// </summary>
[ServiceContract]
public interface IWigiDashWcf
{
    // --- Sensor provider (HWiNFO) ---

    /// <summary>Initializes the HWiNFO provider (SMBus / EC / PresentMon sources).</summary>
    [OperationContract]
    bool InitSensorProvider(bool smbus, bool ec, bool presentmon);

    /// <summary>Releases the HWiNFO provider.</summary>
    [OperationContract]
    bool DeInitSensorProvider();

    /// <summary>Non-zero once the HWiNFO provider has enumerated its sensors.</summary>
    [OperationContract]
    int GetSensorInitStatus();

    /// <summary>The loaded HWiNFO SDK version string, or null when unavailable.</summary>
    [OperationContract]
    string? GetHwinfoSdkVersion();

    /// <summary>The full HWiNFO sensor catalog as the vendor's <c>SensorItem</c> shape.</summary>
    [OperationContract]
    List<VendorSensorItem> GetSensorList();

    // The element names are load-bearing: WCF matches operation parameters by
    // their wire name, not their position. The vendor declares
    // `reading_type`/`sensor_id1`/`sensor_id2`/`IsValid`, so our camelCase
    // parameters map onto the vendor's exact element names through
    // [MessageParameter] (observed on-device 2026-09-13: without this, the
    // request carried our own names, the vendor's serializer defaulted every
    // input to 0, and every reading came back wrong).
    /// <summary>Reads one sensor value; <paramref name="isValid"/> is the vendor's freshness flag.</summary>
    [OperationContract]
    double GetSensorValue(
        [MessageParameter(Name = "reading_type")] int readingType,
        [MessageParameter(Name = "sensor_id1")] int sensorId1,
        [MessageParameter(Name = "sensor_id2")] int sensorId2,
        [MessageParameter(Name = "IsValid")] out bool isValid);
}
