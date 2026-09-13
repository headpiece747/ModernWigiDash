using System.ServiceModel;

namespace ModernWigiDash.Hardware.Service;

/// <summary>
/// The vendor's WCF service contract, mirrored from the live WSDL at
/// http://localhost:8733/WigiDashService/WigiDashWcf/ (pulled 2026-09-13).
/// This is a consumption-only mirror: we never host this endpoint, we only
/// call into it. The field names and method signatures match the vendor's
/// data contracts exactly so a future contract change is visible at compile time.
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
    VendorSensorItem[] GetSensorList();

    [OperationContract]
    (double Value, bool IsValid) GetSensorValue(int readingType, int sensorId1, int sensorId2);

    // --- AIDA64 panel provider ---

    [OperationContract]
    bool InitAidaProvider();

    [OperationContract]
    bool DeInitAidaProvider();

    [OperationContract]
    (bool Ok, byte[]? Buffer) ReadAidaMmap(int offset, int length);

    [OperationContract]
    bool WriteAidaMmap(int offset, byte[] buffer);
}
