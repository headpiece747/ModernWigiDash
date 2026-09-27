// <copyright file="WinUsbBulkDevice.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using System.Runtime.InteropServices;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Owns a WinUSB device handle for direct bulk and control transfers.
/// Implements <see cref="ITransferBackend"/> directly — the adapter is the
/// class, no wrapper needed. Members are virtual so tests can subclass with
/// canned results and drive the transport's connect policy via a fake WinUSB
/// provider in <c>DisplayHidTransport.ProviderFactories</c>. The WinUSB/SetupAPI
/// P/Invoke surface flows through the <see cref="WinUsbApi"/> delegate bag
/// (production: <see cref="WinUsbApi.Default"/>; tests: managed fakes), so the
/// Open failure and cleanup paths are scriptable without hardware.
/// </summary>
internal class WinUsbBulkDevice : ITransferBackend
{
    private IntPtr _deviceHandle = IntPtr.Zero;
    private IntPtr _interfaceHandle = IntPtr.Zero;
    private readonly WinUsbApi _api;

    /// <summary>Production construction: the real P/Invoke surface.</summary>
    public WinUsbBulkDevice()
        : this(WinUsbApi.Default)
    {
    }

    /// <summary>Test seam: an injected P/Invoke surface (see <see cref="WinUsbApi"/>).</summary>
    internal WinUsbBulkDevice(WinUsbApi api)
    {
        _api = api;
    }

    public virtual bool IsOpen => _interfaceHandle != IntPtr.Zero;

    private const string LogCategory = "USB-WINUSB";

    // This leg's file-log vocabulary: the tag binds once here (the DiagLog
    // module), never hand-baked at the call site — line is always
    // "[USB-WINUSB] {message}".
    //
    // The cadence is the flood fix (2026-09-27): every Open() step is logged
    // through this one instance, so a cadence of 1 wrote the whole eight-step
    // SetupAPI sequence on EVERY reconnect attempt. The instance is static, so
    // its counter spans the process: the first Open traces in full, later ones
    // only every BackendDiag.ConnectTraceCadence-th attempt. The attempt's
    // verdict is never suppressed — the engine logs it through ConnectVerdict.
    private static readonly DiagLog _diagnosticLog = new(LogCategory, BackendDiag.ConnectTraceCadence, logFirst: true);

    private static void Log(string msg) => _diagnosticLog.Write(msg);

    // Raw LogCadence (not a DiagLog) so the skipped calls on the 30 FPS bulk
    // path allocate nothing: Due() is an Interlocked, the string is composed
    // only when the line is due.
    private readonly LogCadence _bulkTimingCadence = new(BackendDiag.BulkWriteCadence);

    /// <summary>
    /// Opens the WigiDash device using SetupAPI enumeration and WinUSB initialization.
    /// </summary>
    /// <returns>
    /// <see cref="ConnectionState.Connected"/> on success, otherwise which leg
    /// failed, parsed from the Win32 error at this boundary: enumeration found
    /// nothing or the path was empty
    /// (<see cref="ConnectionState.DeviceNotFound"/>), <c>CreateFileW</c> was
    /// denied with ERROR_ACCESS_DENIED / 5 — another client owns the handle
    /// (<see cref="ConnectionState.DeviceBusy"/>), or the handle opened but
    /// <c>WinUsb_Initialize</c> failed
    /// (<see cref="ConnectionState.DeviceNotResponding"/>). The distinction
    /// matters to the user: a busy device is present and needs the other app to
    /// let go, a missing one needs a cable.
    /// </returns>
    public virtual ConnectionState Open(Guid interfaceGuid)
    {
        if (IsOpen)
            return ConnectionState.Connected;

        try
        {
            SetupApiNative.NativeGuid guidStruct = SetupApiNative.NativeGuid.FromGuid(interfaceGuid);
            Log($"FromGuid: Data1=0x{guidStruct.Data1:X8} Data2=0x{guidStruct.Data2:X4} Data3=0x{guidStruct.Data3:X4} Data4=[{string.Join(",", guidStruct.Data4.Select(b => b.ToString("X2")))}]");

            IntPtr deviceInfo = _api.GetClassDevs(
                ref guidStruct, null, IntPtr.Zero,
                SetupApiNative.DigcfPresent | SetupApiNative.DigcfDeviceInterface);

            if (deviceInfo == SetupApiNative.InvalidHandleValue)
            {
                int error = Marshal.GetLastWin32Error();
                Log($"SetupDiGetClassDevsW failed: GetLastError={error} (0x{error:X8})");
                return ConnectionState.DeviceNotFound;
            }

            Log($"SetupDiGetClassDevsW succeeded: deviceInfo=0x{deviceInfo.ToInt64():X}");

            try
            {
                SetupApiNative.SpDeviceInterfaceData ifaceData = default;
                ifaceData.CbSize = (uint)Marshal.SizeOf<SetupApiNative.SpDeviceInterfaceData>();
                ifaceData.InterfaceClassGuid = guidStruct;
                Log($"SpDeviceInterfaceData CbSize={ifaceData.CbSize}");

                if (!_api.EnumDeviceInterfaces(
                    deviceInfo, IntPtr.Zero, ref guidStruct, 0, ref ifaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    Log($"SetupDiEnumDeviceInterfaces failed: GetLastError={error} (0x{error:X8})");
                    return ConnectionState.DeviceNotFound;
                }

                Log($"SetupDiEnumDeviceInterfaces succeeded: Flags=0x{ifaceData.Flags:X}");

                // Get required buffer size.
                // This call is expected to fail with ERROR_INSUFFICIENT_BUFFER (122)
                // when querying with a NULL buffer — that IS the success path.
                _api.GetDeviceInterfaceDetail(
                    deviceInfo, ref ifaceData, IntPtr.Zero, 0, out uint requiredSize, IntPtr.Zero);
                int sizeQueryError = Marshal.GetLastWin32Error();
                Log($"SetupDiGetDeviceInterfaceDetailW size query: requiredSize={requiredSize} GetLastError={sizeQueryError} (0x{sizeQueryError:X8})");

                if (requiredSize == 0)
                {
                    Log("SetupDiGetDeviceInterfaceDetailW returned requiredSize=0, no device");
                    return ConnectionState.DeviceNotFound;
                }

                // Allocate buffer and get device path
                IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W layout:
                    //   DWORD cbSize (4 bytes) — must equal sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)
                    //   WCHAR DevicePath[requiredSize - 4] — variable-length null-terminated string
                    // Microsoft docs: "cbSize must be set to sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W)" —
                    // the struct size WITHOUT the path (8 on x64, 6 on x86). Setting it to
                    // requiredSize (the full buffer, 160 here) fails with ERROR_INVALID_USER_BUFFER (1784).
                    int detailCbSize = Marshal.SizeOf<SetupApiNative.SpDeviceInterfaceDetailData>();
                    Log($"SetupDiGetDeviceInterfaceDetailW: cbSize={detailCbSize} requiredSize={requiredSize} buffer={requiredSize}");
                    Marshal.WriteInt32(detailBuffer, detailCbSize);

                    if (!_api.GetDeviceInterfaceDetail(
                        deviceInfo, ref ifaceData, detailBuffer, requiredSize, out _, IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        Log($"SetupDiGetDeviceInterfaceDetailW (get path) failed: GetLastError={error} (0x{error:X8})");
                        return ConnectionState.DeviceNotFound;
                    }

                    // Device path string starts right after the cbSize DWORD (at offset 4)
                    const int cbSizeOffset = 4;
                    string devicePath = Marshal.PtrToStringUni(detailBuffer + cbSizeOffset) ?? "";
                    Log($"Device path: {devicePath}");

                    if (string.IsNullOrEmpty(devicePath))
                    {
                        Log("Device path is empty");
                        return ConnectionState.DeviceNotFound;
                    }

                    // Open device with shared access.
                    // FILE_FLAG_OVERLAPPED is REQUIRED: WinUsb_Initialize fails
                    // with ERROR_INVALID_HANDLE when the handle was not opened
                    // for overlapped I/O (see WinUsb_Initialize docs). WinUSB
                    // reads the FILE_FLAG_OVERLAPPED bit off the handle to
                    // decide how to process its internal IOCTLs.
                    _deviceHandle = _api.CreateFile(
                        devicePath,
                        SetupApiNative.GenericRead | SetupApiNative.GenericWrite,
                        SetupApiNative.FileShareRead | SetupApiNative.FileShareWrite,
                        IntPtr.Zero,
                        SetupApiNative.OpenExisting,
                        SetupApiNative.FileAttributeNormal | SetupApiNative.FileFlagOverlapped,
                        IntPtr.Zero);

                    if (_deviceHandle == SetupApiNative.InvalidHandleValue)
                    {
                        // Read through the api bag, not Marshal directly: this
                        // value decides the reported state, so it has to be
                        // drivable from a test (see WinUsbApi.GetLastError).
                        int error = _api.GetLastError();
                        Log($"CreateFileW failed: GetLastError={error} (0x{error:X8})");
                        // ERROR_ACCESS_DENIED (5) is the load-bearing case: the
                        // enumeration FOUND the display and the path is right, so
                        // the only reason the open is refused is another client
                        // holding the handle. Reporting that as "no device" is
                        // what made a present, busy display read as a missing
                        // one (the 2026-09-27 incident). Every other Win32 error
                        // on this call is treated as "not openable" — the
                        // enumeration already proved the device is there, so
                        // DeviceNotResponding is the honest bucket.
                        return error == SetupApiNative.ErrorAccessDenied
                            ? ConnectionState.DeviceBusy
                            : ConnectionState.DeviceNotResponding;
                    }

                    Log($"CreateFileW succeeded: handle=0x{_deviceHandle.ToInt64():X}");

                    // Initialize WinUSB
                    if (!_api.Initialize(_deviceHandle, out _interfaceHandle))
                    {
                        int error = Marshal.GetLastWin32Error();
                        Log($"WinUsb_Initialize failed: GetLastError={error} (0x{error:X8})");
                        _api.CloseHandle(_deviceHandle);
                        _deviceHandle = IntPtr.Zero;
                        return ConnectionState.DeviceNotResponding;
                    }

                    Log($"WinUsb_Initialize succeeded: interfaceHandle=0x{_interfaceHandle.ToInt64():X}");

                    // Set pipe timeouts
                    IntPtr timeoutPtr = Marshal.AllocHGlobal(sizeof(int));
                    try
                    {
                        // Control pipe timeout (the protocol constants own the budget the
                        // transport's CloseBound derives from).
                        Marshal.WriteInt32(timeoutPtr, DisplayProtocolConstants.ControlPipeTimeoutMs);
                        _api.SetPipePolicy(
                            _interfaceHandle, 0x00, WinUsbNative.PipeTransferTimeout, sizeof(int), timeoutPtr);

                        // Bulk OUT pipe timeout (large-transfer budget, 30s).
                        Marshal.WriteInt32(timeoutPtr, DisplayProtocolConstants.BulkPipeTimeoutMs);
                        _api.SetPipePolicy(
                            _interfaceHandle, 0x01, WinUsbNative.PipeTransferTimeout, sizeof(int), timeoutPtr);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(timeoutPtr);
                    }

                    Log($"Pipe timeouts configured (control={DisplayProtocolConstants.ControlPipeTimeoutMs}ms, bulk={DisplayProtocolConstants.BulkPipeTimeoutMs}ms)");
                    return ConnectionState.Connected;
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
            finally
            {
                _api.DestroyDeviceInfoList(deviceInfo);
            }
        }
        catch (Exception ex)
        {
            Log($"Exception during Open: {ex.Message}");
            // Self-contained partial-state cleanup: an exception after
            // CreateFile or WinUsb_Initialize would otherwise leave live
            // handles open. Idempotent with the leg's Dispose on the failure
            // return — the handles are zeroed, so the second call is a no-op.
            // A throw mid-open is a device that would not cooperate, not one
            // that is absent, so it reports as not-responding.
            Dispose();
            return ConnectionState.DeviceNotResponding;
        }
    }

    /// <summary>
    /// Performs a synchronous bulk OUT transfer. Sends the entire buffer in a
    /// single WinUsb_WritePipe call. The buffer is pinned with fixed (no
    /// GCHandle alloc/free on the per-frame path); AllowUnsafeBlocks is
    /// solution-wide.
    /// </summary>
    public virtual bool BulkWrite(byte pipeId, byte[] data, out int transferred)
    {
        transferred = 0;
        if (!IsOpen)
            return false;

        try
        {
            // The fixed pin replaces the old GCHandle alloc/free: one
            // per-frame allocation pair on the 30 FPS path, retired.
#pragma warning disable S6640 // zero-alloc bulk write fast path (the FrameEncoder precedent)
            unsafe
            {
                fixed (byte* pinned = data)
                {
                    // Zero-alloc timing: GetTimestamp/GetElapsedTime avoid the
                    // Stopwatch.StartNew allocation on the per-frame bulk write path.
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    bool ok = _api.WritePipe(
                        _interfaceHandle, pipeId,
                        (IntPtr)pinned,
                        (uint)data.Length,
                        out uint bytesTransferred,
                        IntPtr.Zero); // Synchronous (no OVERLAPPED)
                    long elapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                    if (_bulkTimingCadence.Due())
                        Log($"BulkWrite {data.Length} bytes took {elapsedMs} ms (ok={ok})");

                    if (!ok)
                        Log($"BulkWrite failed: ok=false transferred={bytesTransferred}/{data.Length}, error={Marshal.GetLastWin32Error()}");
                    else if (bytesTransferred != data.Length)
                        Log($"BulkWrite short write: transferred={bytesTransferred}/{data.Length}, error={Marshal.GetLastWin32Error()}");

                    // A short write is a failed write — the caller (SendFrame) routes
                    // the failure to CmdFrameAbort, mirroring the LibUsb backend's
                    // full-transfer requirement.
                    transferred = (int)bytesTransferred;
                    return ok && bytesTransferred == data.Length;
                }
            }
#pragma warning restore S6640
        }
        catch (Exception ex)
        {
            Log($"BulkWrite exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Control OUT transfer (vendor command).
    /// </summary>
    public virtual bool ControlOut(byte request, ushort wValue, byte[]? data)
    {
        if (!IsOpen)
            return false;

        WinUsbNative.WinUsbSetupPacket setup = default;
        setup.RequestType = DisplayProtocolConstants.VendorOutRequestType; // Class | Interface | Host-to-Device
        setup.Request = request;
        setup.Value = wValue;
        setup.Index = 0;
        setup.Length = (ushort)(data?.Length ?? 0);

        byte[] buffer = data?.Length > 0 ? data : [];

        return _api.ControlTransfer(
            _interfaceHandle, setup, buffer, (uint)buffer.Length, out _, IntPtr.Zero);
    }

    /// <summary>
    /// Control IN transfer (vendor query).
    /// </summary>
    public virtual bool ControlIn(byte request, byte[] buffer, out int transferred, ushort wValue = 0, ushort wIndex = 0)
    {
        transferred = 0;
        if (!IsOpen)
            return false;

        WinUsbNative.WinUsbSetupPacket setup = default;
        setup.RequestType = DisplayProtocolConstants.ControlInRequestType; // Vendor | Device-to-Host | Interface
        setup.Request = request;
        setup.Value = wValue;
        setup.Index = wIndex;
        setup.Length = (ushort)buffer.Length;

        // A zero-byte control-in reads as failure (matches the LibUsb backend's
        // transferred > 0 semantics) — the PING depends on both backends agreeing.
        bool ok = _api.ControlTransfer(
            _interfaceHandle, setup, buffer, (uint)buffer.Length, out uint transferredCount, IntPtr.Zero);
        transferred = (int)transferredCount;
        return ok && transferred > 0;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Test seam: subclasses override to observe teardown.</summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_interfaceHandle != IntPtr.Zero)
        {
            _api.Free(_interfaceHandle);
            _interfaceHandle = IntPtr.Zero;
        }

        if (_deviceHandle != IntPtr.Zero && _deviceHandle != SetupApiNative.InvalidHandleValue)
        {
            _api.CloseHandle(_deviceHandle);
            _deviceHandle = IntPtr.Zero;
        }
    }
}
