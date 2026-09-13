using System.Collections.Generic;
using System.ServiceModel;

namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The vendor's WCF service contract, mirrored from the decompiled vendor service
/// (WigiDashWcf.dll, ElmorLabs v1.0.0.2) and verified against the live XSD at
/// http://localhost:8733/WigiDashService/WigiDashWcf/?xsd=xsd0 (pulled 2026-09-13).
/// This is a consumption-only mirror: we never host this endpoint, we only call
/// into it. The signatures use <c>out</c> parameters (not tuples) to match the
/// vendor exactly: WCF serializes an <c>out byte[] buffer</c> as a separate
/// &lt;buffer&gt; element in the response, whereas a C# tuple would serialize as a
/// single composite object, which is what broke deserialization on-device
/// (the "Encountered 'Text'" fault at position 147). Field names and method
/// signatures match the vendor's data contracts exactly so a future contract
/// change is visible at compile time.
/// </summary>
[ServiceContract]
public interface IWigiDashWcf
{
    // --- Sensor provider (HWiNFO) ---

    [OperationContract]
    bool InitSensorProvider(bool smbus, bool ec, bool presentmon);

    [OperationContract]
    bool DeInitSensorProvider();

    [OperationContract]
    int GetSensorInitStatus();

    [OperationContract]
    string? GetHwinfoSdkVersion();

    [OperationContract]
    List<VendorSensorItem> GetSensorList();

    [OperationContract]
    double GetSensorValue(int readingType, int sensorId1, int sensorId2, out bool isValid);

    // --- AIDA64 panel provider ---

    [OperationContract]
    bool InitAidaProvider();

    [OperationContract]
    bool DeInitAidaProvider();

    [OperationContract]
    bool ReadAidaMmap(int offset, int length, out byte[]? buffer);

    [OperationContract]
    bool WriteAidaMmap(int offset, byte[] buffer);
}
