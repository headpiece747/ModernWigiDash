// <copyright file="WinUsbNative.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using System.Runtime.InteropServices;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Direct WinUSB P/Invoke for bulk and control transfers.
/// Uses WinUsb_WritePipe synchronously to avoid LibUsbDotNet's problematic
/// WinUsb_WritePipe_Overlapped call.
/// </summary>
internal static class WinUsbNative
{
    private const string WinUsbDll = "winusb.dll";

    [DllImport(WinUsbDll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_Initialize(IntPtr DeviceHandle, out IntPtr InterfaceHandle);

    [DllImport(WinUsbDll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_Free(IntPtr InterfaceHandle);

    [DllImport(WinUsbDll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_SetPipePolicy(
        IntPtr InterfaceHandle, byte PipeId, uint Id, uint Length, IntPtr Value);

    public const uint PipeTransferTimeout = 3;

    /// <summary>
    /// Synchronous bulk write using WinUsb_WritePipe with a pinned IntPtr buffer.
    /// </summary>
    [DllImport(WinUsbDll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_WritePipe(
        IntPtr InterfaceHandle,
        byte PipeId,
        IntPtr Buffer,
        uint BufferLength,
        out uint NumberOfBytesTransferred,
        IntPtr Overlapped);

    [StructLayout(LayoutKind.Sequential)]
    public struct WinUsbSetupPacket
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
    }

    [DllImport(WinUsbDll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WinUsb_ControlTransfer(
        IntPtr InterfaceHandle,
        [In] WinUsbSetupPacket SetupPacket,
        [In, Out] byte[] Buffer,
        uint BufferLength,
        out uint NumberOfBytesTransferred,
        IntPtr Overlapped);
}

/// <summary>
/// SetupAPI P/Invoke for device enumeration.
/// </summary>
internal static class SetupApiNative
{
    private const string SetupApiDll = "setupapi.dll";
    private const string Kernel32Dll = "kernel32.dll";

    public const uint DigcfPresent = 0x00000002;
    public const uint DigcfDeviceInterface = 0x00000010;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x00000001;
    public const uint FileShareWrite = 0x00000002;
    public const uint OpenExisting = 3;
    public const uint FileAttributeNormal = 0x80;
    public static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>
    /// Native GUID struct for SetupAPI P/Invoke.
    /// Uses MarshalAs ByValArray for Data4 since C# doesn't allow
    /// taking the address of a field in a local struct variable.
    /// Layout is identical to SYSTEM_GUID: 16 bytes, no padding.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NativeGuid
    {
        public uint Data1;
        public ushort Data2;
        public ushort Data3;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] Data4;

        public static NativeGuid FromGuid(Guid guid)
        {
            byte[] b = guid.ToByteArray();
            return new NativeGuid
            {
                Data1 = BitConverter.ToUInt32(b, 0),
                Data2 = BitConverter.ToUInt16(b, 4),
                Data3 = BitConverter.ToUInt16(b, 6),
                Data4 = b[8..]
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public NativeGuid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport(SetupApiDll, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevsW(
        ref NativeGuid ClassGuid, string? Enumerator, IntPtr HwndParent, uint Flags);

    [DllImport(SetupApiDll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref NativeGuid InterfaceClassGuid,
        uint MemberIndex, ref SpDeviceInterfaceData DeviceInterfaceData);

    [DllImport(SetupApiDll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr DeviceInfoSet, ref SpDeviceInterfaceData DeviceInterfaceData,
        IntPtr DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize,
        out uint RequiredSize, IntPtr RelatedDeviceInfoData);

    [DllImport(SetupApiDll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport(Kernel32Dll, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport(Kernel32Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);
}

/// <summary>
/// Owns a WinUSB device handle for direct bulk and control transfers.
/// </summary>
internal sealed class WinUsbBulkDevice : IDisposable
{
    private IntPtr _deviceHandle = IntPtr.Zero;
    private IntPtr _interfaceHandle = IntPtr.Zero;

    public IntPtr InterfaceHandle => _interfaceHandle;
    public bool IsOpen => _interfaceHandle != IntPtr.Zero;

    private static void Log(string msg)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "display_device.log");
            using var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [USB-WINUSB] {msg}");
        }
        catch (IOException)
        {
            // Log file may be locked or unavailable; silently ignore
        }
    }

    /// <summary>
    /// Opens the WigiDash device using SetupAPI enumeration and WinUSB initialization.
    /// </summary>
    public bool Open(Guid interfaceGuid)
    {
        if (IsOpen)
            return true;

        try
        {
            SetupApiNative.NativeGuid guidStruct = SetupApiNative.NativeGuid.FromGuid(interfaceGuid);
            Log($"FromGuid: Data1=0x{guidStruct.Data1:X8} Data2=0x{guidStruct.Data2:X4} Data3=0x{guidStruct.Data3:X4} Data4=[{string.Join(",", guidStruct.Data4.Select(b => b.ToString("X2")))}]");

            IntPtr deviceInfo = SetupApiNative.SetupDiGetClassDevsW(
                ref guidStruct, null, IntPtr.Zero,
                SetupApiNative.DigcfPresent | SetupApiNative.DigcfDeviceInterface);

            if (deviceInfo == SetupApiNative.InvalidHandleValue)
            {
                int error = Marshal.GetLastWin32Error();
                Log($"SetupDiGetClassDevsW failed: GetLastError={error} (0x{error:X8})");
                return false;
            }

            Log($"SetupDiGetClassDevsW succeeded: deviceInfo=0x{deviceInfo.ToInt64():X}");

            try
            {
                SetupApiNative.SpDeviceInterfaceData ifaceData = default;
                ifaceData.CbSize = (uint)Marshal.SizeOf<SetupApiNative.SpDeviceInterfaceData>();
                ifaceData.InterfaceClassGuid = guidStruct;
                Log($"SpDeviceInterfaceData CbSize={ifaceData.CbSize}");

                if (!SetupApiNative.SetupDiEnumDeviceInterfaces(
                    deviceInfo, IntPtr.Zero, ref guidStruct, 0, ref ifaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    Log($"SetupDiEnumDeviceInterfaces failed: GetLastError={error} (0x{error:X8})");
                    return false;
                }

                Log($"SetupDiEnumDeviceInterfaces succeeded: Flags=0x{ifaceData.Flags:X}");

                // Get required buffer size.
                // This call is expected to fail with ERROR_INSUFFICIENT_BUFFER (122)
                // when querying with a NULL buffer — that IS the success path.
                SetupApiNative.SetupDiGetDeviceInterfaceDetailW(
                    deviceInfo, ref ifaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);

                int sizeQueryError = Marshal.GetLastWin32Error();
                Log($"SetupDiGetDeviceInterfaceDetailW size query: requiredSize={requiredSize} GetLastError={sizeQueryError} (0x{sizeQueryError:X8})");

                if (requiredSize == 0)
                {
                    Log("SetupDiGetDeviceInterfaceDetailW returned requiredSize=0, no device");
                    return false;
                }

                // Allocate buffer and get device path
                IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W layout:
                    //   DWORD cbSize (4 bytes) — must equal the full struct size
                    //   WCHAR DevicePath[requiredSize - 4] — variable-length null-terminated string
                    // Microsoft docs: "cbSize must be set to sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA)
                    //   - sizeof(TCHAR) + (size of the device path string, in TCHARs)"
                    // For Unicode: cbSize = 4 + (pathChars * 2) = requiredSize
                    Marshal.WriteInt32(detailBuffer, (int)requiredSize);

                    if (!SetupApiNative.SetupDiGetDeviceInterfaceDetailW(
                        deviceInfo, ref ifaceData, detailBuffer, requiredSize, out _, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        Log($"SetupDiGetDeviceInterfaceDetailW (get path) failed: GetLastError={error} (0x{error:X8})");
                        return false;
                    }

                    // Device path string starts right after the cbSize DWORD (at offset 4)
                    const int cbSizeOffset = 4;
                    string devicePath = Marshal.PtrToStringUni(detailBuffer + cbSizeOffset) ?? "";
                    Log($"Device path: {devicePath}");

                    if (string.IsNullOrEmpty(devicePath))
                    {
                        Log("Device path is empty");
                        return false;
                    }

                    // Open device with shared access
                    _deviceHandle = SetupApiNative.CreateFileW(
                        devicePath,
                        SetupApiNative.GenericRead | SetupApiNative.GenericWrite,
                        SetupApiNative.FileShareRead | SetupApiNative.FileShareWrite,
                        IntPtr.Zero,
                        SetupApiNative.OpenExisting,
                        SetupApiNative.FileAttributeNormal,
                        IntPtr.Zero);

                    if (_deviceHandle == SetupApiNative.InvalidHandleValue)
                    {
                        int error = Marshal.GetLastWin32Error();
                        Log($"CreateFileW failed: GetLastError={error} (0x{error:X8})");
                        return false;
                    }

                    Log($"CreateFileW succeeded: handle=0x{_deviceHandle.ToInt64():X}");

                    // Initialize WinUSB
                    if (!WinUsbNative.WinUsb_Initialize(_deviceHandle, out _interfaceHandle))
                    {
                        int error = Marshal.GetLastWin32Error();
                        Log($"WinUsb_Initialize failed: GetLastError={error} (0x{error:X8})");
                        SetupApiNative.CloseHandle(_deviceHandle);
                        _deviceHandle = IntPtr.Zero;
                        return false;
                    }

                    Log($"WinUsb_Initialize succeeded: interfaceHandle=0x{_interfaceHandle.ToInt64():X}");

                    // Set pipe timeouts
                    IntPtr timeoutPtr = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        // Control pipe timeout: 1000ms
                        Marshal.WriteInt32(timeoutPtr, 1000);
                        WinUsbNative.WinUsb_SetPipePolicy(
                            _interfaceHandle, 0x00, WinUsbNative.PipeTransferTimeout, sizeof(int), timeoutPtr);

                        // Bulk OUT pipe timeout: 30000ms (30s for large transfers)
                        Marshal.WriteInt32(timeoutPtr, 30000);
                        WinUsbNative.WinUsb_SetPipePolicy(
                            _interfaceHandle, 0x01, WinUsbNative.PipeTransferTimeout, sizeof(int), timeoutPtr);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(timeoutPtr);
                    }

                    Log("Pipe timeouts configured (control=1000ms, bulk=30000ms)");
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
            finally
            {
                SetupApiNative.SetupDiDestroyDeviceInfoList(deviceInfo);
            }
        }
        catch (Exception ex)
        {
            Log($"Exception during Open: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Performs a synchronous bulk OUT transfer using pinned memory.
    /// Sends the entire buffer in a single WinUsb_WritePipe call.
    /// </summary>
    public bool BulkWrite(byte pipeId, byte[] data, out int transferred)
    {
        transferred = 0;
        if (!IsOpen)
            return false;

        GCHandle handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            bool ok = WinUsbNative.WinUsb_WritePipe(
                _interfaceHandle, pipeId,
                handle.AddrOfPinnedObject(),
                (uint)data.Length,
                out uint bytesTransferred,
                IntPtr.Zero); // Synchronous (no OVERLAPPED)

            transferred = (int)bytesTransferred;
            return ok;
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>
    /// Control OUT transfer (vendor command).
    /// </summary>
    public bool ControlOut(byte request, ushort wValue, byte[]? data)
    {
        if (!IsOpen)
            return false;

        WinUsbNative.WinUsbSetupPacket setup = default;
        setup.RequestType = 0x21; // Vendor | Host-to-Device | Interface
        setup.Request = request;
        setup.Value = wValue;
        setup.Index = 0;
        setup.Length = (ushort)(data?.Length ?? 0);

        byte[] buffer = data?.Length > 0 ? data : Array.Empty<byte>();

        return WinUsbNative.WinUsb_ControlTransfer(
            _interfaceHandle, setup, buffer, (uint)buffer.Length, out _, IntPtr.Zero);
    }

    /// <summary>
    /// Control IN transfer (vendor query).
    /// </summary>
    public bool ControlIn(byte request, byte[] buffer, ushort wValue = 0, ushort wIndex = 0)
    {
        if (!IsOpen)
            return false;

        WinUsbNative.WinUsbSetupPacket setup = default;
        setup.RequestType = 0xA1; // Vendor | Device-to-Host | Interface
        setup.Request = request;
        setup.Value = wValue;
        setup.Index = wIndex;
        setup.Length = (ushort)buffer.Length;

        return WinUsbNative.WinUsb_ControlTransfer(
            _interfaceHandle, setup, buffer, (uint)buffer.Length, out _, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_interfaceHandle != IntPtr.Zero)
        {
            WinUsbNative.WinUsb_Free(_interfaceHandle);
            _interfaceHandle = IntPtr.Zero;
        }

        if (_deviceHandle != IntPtr.Zero && _deviceHandle != SetupApiNative.InvalidHandleValue)
        {
            SetupApiNative.CloseHandle(_deviceHandle);
            _deviceHandle = IntPtr.Zero;
        }
    }
}
