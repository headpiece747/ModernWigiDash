namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// The WinUSB/SetupAPI P/Invoke surface as an injectable delegate bag (the
/// PresentMonNative precedent): production binds the real externs once via
/// <see cref="Default"/>; tests inject managed fakes, so
/// <see cref="WinUsbBulkDevice.Open"/>'s failure and cleanup paths — the
/// riskiest untested code in the stack — are scriptable without hardware.
/// </summary>
internal sealed class WinUsbApi
{
    public delegate bool InitializeFn(IntPtr deviceHandle, out IntPtr interfaceHandle);
    public delegate bool FreeFn(IntPtr interfaceHandle);
    public delegate bool SetPipePolicyFn(IntPtr interfaceHandle, byte pipeId, uint id, uint length, IntPtr value);
    public delegate bool WritePipeFn(IntPtr interfaceHandle, byte pipeId, IntPtr buffer, uint bufferLength, out uint numberOfBytesTransferred, IntPtr overlapped);
    public delegate bool ControlTransferFn(IntPtr interfaceHandle, WinUsbNative.WinUsbSetupPacket setupPacket, byte[] buffer, uint bufferLength, out uint numberOfBytesTransferred, IntPtr overlapped);
    public delegate IntPtr GetClassDevsFn(ref SetupApiNative.NativeGuid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
    public delegate bool EnumDeviceInterfacesFn(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref SetupApiNative.NativeGuid interfaceClassGuid, uint memberIndex, ref SetupApiNative.SpDeviceInterfaceData deviceInterfaceData);
    public delegate bool GetDeviceInterfaceDetailFn(IntPtr deviceInfoSet, ref SetupApiNative.SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr relatedDeviceInfoData);
    public delegate bool DestroyDeviceInfoListFn(IntPtr deviceInfoSet);
    public delegate IntPtr CreateFileFn(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    public delegate bool CloseHandleFn(IntPtr handle);

    /// <summary>The production binding: the real P/Invoke externs.</summary>
    public static readonly WinUsbApi Default = new(
        WinUsbNative.WinUsb_Initialize,
        WinUsbNative.WinUsb_Free,
        WinUsbNative.WinUsb_SetPipePolicy,
        WinUsbNative.WinUsb_WritePipe,
        WinUsbNative.WinUsb_ControlTransfer,
        SetupApiNative.SetupDiGetClassDevsW,
        SetupApiNative.SetupDiEnumDeviceInterfaces,
        SetupApiNative.SetupDiGetDeviceInterfaceDetailW,
        SetupApiNative.SetupDiDestroyDeviceInfoList,
        SetupApiNative.CreateFileW,
        SetupApiNative.CloseHandle);

    public InitializeFn Initialize { get; }
    public FreeFn Free { get; }
    public SetPipePolicyFn SetPipePolicy { get; }
    public WritePipeFn WritePipe { get; }
    public ControlTransferFn ControlTransfer { get; }
    public GetClassDevsFn GetClassDevs { get; }
    public EnumDeviceInterfacesFn EnumDeviceInterfaces { get; }
    public GetDeviceInterfaceDetailFn GetDeviceInterfaceDetail { get; }
    public DestroyDeviceInfoListFn DestroyDeviceInfoList { get; }
    public CreateFileFn CreateFile { get; }
    public CloseHandleFn CloseHandle { get; }

    public WinUsbApi(
        InitializeFn initialize,
        FreeFn free,
        SetPipePolicyFn setPipePolicy,
        WritePipeFn writePipe,
        ControlTransferFn controlTransfer,
        GetClassDevsFn getClassDevs,
        EnumDeviceInterfacesFn enumDeviceInterfaces,
        GetDeviceInterfaceDetailFn getDeviceInterfaceDetail,
        DestroyDeviceInfoListFn destroyDeviceInfoList,
        CreateFileFn createFile,
        CloseHandleFn closeHandle)
    {
        Initialize = initialize;
        Free = free;
        SetPipePolicy = setPipePolicy;
        WritePipe = writePipe;
        ControlTransfer = controlTransfer;
        GetClassDevs = getClassDevs;
        EnumDeviceInterfaces = enumDeviceInterfaces;
        GetDeviceInterfaceDetail = getDeviceInterfaceDetail;
        DestroyDeviceInfoList = destroyDeviceInfoList;
        CreateFile = createFile;
        CloseHandle = closeHandle;
    }
}
