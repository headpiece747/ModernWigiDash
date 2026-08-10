namespace ModernWigiDash.Hardware.Transport;

internal delegate bool WinUsbInitializeFn(IntPtr deviceHandle, out IntPtr interfaceHandle);
internal delegate bool WinUsbFreeFn(IntPtr interfaceHandle);
internal delegate bool WinUsbSetPipePolicyFn(IntPtr interfaceHandle, byte pipeId, uint id, uint length, IntPtr value);
internal delegate bool WinUsbWritePipeFn(IntPtr interfaceHandle, byte pipeId, IntPtr buffer, uint bufferLength, out uint numberOfBytesTransferred, IntPtr overlapped);
internal delegate bool WinUsbControlTransferFn(IntPtr interfaceHandle, WinUsbNative.WinUsbSetupPacket setupPacket, byte[] buffer, uint bufferLength, out uint numberOfBytesTransferred, IntPtr overlapped);
internal delegate IntPtr WinUsbGetClassDevsFn(ref SetupApiNative.NativeGuid classGuid, string? enumerator, IntPtr hwndParent, uint flags);
internal delegate bool WinUsbEnumDeviceInterfacesFn(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref SetupApiNative.NativeGuid interfaceClassGuid, uint memberIndex, ref SetupApiNative.SpDeviceInterfaceData deviceInterfaceData);
internal delegate bool WinUsbGetDeviceInterfaceDetailFn(IntPtr deviceInfoSet, ref SetupApiNative.SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr relatedDeviceInfoData);
internal delegate bool WinUsbDestroyDeviceInfoListFn(IntPtr deviceInfoSet);
internal delegate IntPtr WinUsbCreateFileFn(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
internal delegate bool WinUsbCloseHandleFn(IntPtr handle);

/// <summary>
/// The WinUSB/SetupAPI P/Invoke surface as an injectable delegate bag (the
/// PresentMonNative precedent): production binds the real externs once via
/// <see cref="Default"/>; tests inject managed fakes, so
/// <see cref="WinUsbBulkDevice.Open"/>'s failure and cleanup paths — the
/// riskiest untested code in the stack — are scriptable without hardware.
/// </summary>
internal sealed class WinUsbApi(
    WinUsbInitializeFn initialize,
    WinUsbFreeFn free,
    WinUsbSetPipePolicyFn setPipePolicy,
    WinUsbWritePipeFn writePipe,
    WinUsbControlTransferFn controlTransfer,
    WinUsbGetClassDevsFn getClassDevs,
    WinUsbEnumDeviceInterfacesFn enumDeviceInterfaces,
    WinUsbGetDeviceInterfaceDetailFn getDeviceInterfaceDetail,
    WinUsbDestroyDeviceInfoListFn destroyDeviceInfoList,
    WinUsbCreateFileFn createFile,
    WinUsbCloseHandleFn closeHandle)
{
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

    internal WinUsbInitializeFn Initialize { get; } = initialize;
    internal WinUsbFreeFn Free { get; } = free;
    internal WinUsbSetPipePolicyFn SetPipePolicy { get; } = setPipePolicy;
    internal WinUsbWritePipeFn WritePipe { get; } = writePipe;
    internal WinUsbControlTransferFn ControlTransfer { get; } = controlTransfer;
    internal WinUsbGetClassDevsFn GetClassDevs { get; } = getClassDevs;
    internal WinUsbEnumDeviceInterfacesFn EnumDeviceInterfaces { get; } = enumDeviceInterfaces;
    internal WinUsbGetDeviceInterfaceDetailFn GetDeviceInterfaceDetail { get; } = getDeviceInterfaceDetail;
    internal WinUsbDestroyDeviceInfoListFn DestroyDeviceInfoList { get; } = destroyDeviceInfoList;
    internal WinUsbCreateFileFn CreateFile { get; } = createFile;
    internal WinUsbCloseHandleFn CloseHandle { get; } = closeHandle;
}
