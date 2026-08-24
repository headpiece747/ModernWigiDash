using System.Runtime.InteropServices;
using System.Text;
using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

/// <summary>
/// Drives <see cref="WinUsbBulkDevice"/> through the <see cref="WinUsbApi"/>
/// delegate-bag seam — the SetupAPI/WinUSB P/Invoke surface is replaced with
/// scripted fakes, so <see cref="WinUsbBulkDevice.Open"/>'s failure and cleanup
/// paths (the riskiest untested code in the stack) are asserted without
/// hardware, alongside the transferred-semantics the PING depends on.
/// </summary>
[TestClass]
public class WinUsbBulkDeviceTests
{

    /// <summary>
    /// Scripted WinUSB/SetupAPI surface: every P/Invoke becomes a recorded,
    /// configurable call. Defaults walk the full Open success path; tests flip
    /// the individual results they want to fail.
    /// </summary>
    private sealed class ApiScript
    {
        public IntPtr DeviceInfoSet { get; } = new(1);
        public IntPtr DeviceHandle { get; } = new(2);
        public IntPtr InterfaceHandle { get; } = new(3);

        public bool GetClassDevsResult { get; set; } = true;
        public bool EnumInterfacesResult { get; set; } = true;
        public uint RequiredSize { get; set; } = 160;
        public bool PathQueryResult { get; set; } = true;
        public string DevicePath { get; set; } = """\\\\.\\GLOBALROOT\\Device\\WigiDash""";
        public bool CreateFileResult { get; set; } = true;
        public bool InitializeResult { get; set; } = true;
        public bool SetPipePolicyThrows { get; set; }
        public uint? WritePipeTransferred { get; set; }
        public bool WritePipeResult { get; set; } = true;
        public uint? ControlTransferBytes { get; set; }
        public bool ControlTransferResult { get; set; } = true;

        public int GetClassDevsCalls { get; private set; }
        public int EnumCalls { get; private set; }
        public int SizeQueryCalls { get; private set; }
        public int PathQueryCalls { get; private set; }
        public int DestroyListCalls { get; private set; }
        public int CreateFileCalls { get; private set; }
        public int InitializeCalls { get; private set; }
        public int FreeCalls { get; private set; }
        public int CloseHandleCalls { get; private set; }
        public int CbSizeWritten { get; private set; } = -1;
        public uint CreateFileFlags { get; private set; }
        public List<(byte PipeId, int TimeoutMs)> PipePolicies { get; } = [];

        public WinUsbApi ToApi() => new(
            initialize: (IntPtr deviceHandle, out IntPtr interfaceHandle) =>
            {
                InitializeCalls++;
                interfaceHandle = InitializeResult ? InterfaceHandle : IntPtr.Zero;
                return InitializeResult;
            },
            free: _ =>
            {
                FreeCalls++;
                return true;
            },
            setPipePolicy: (IntPtr interfaceHandle, byte pipeId, uint id, uint length, IntPtr value) =>
            {
                if (SetPipePolicyThrows)
                    throw new InvalidOperationException("scripted SetPipePolicy failure");
                PipePolicies.Add((pipeId, Marshal.ReadInt32(value)));
                return true;
            },
            writePipe: (IntPtr interfaceHandle, byte pipeId, IntPtr buffer, uint bufferLength, out uint transferred, IntPtr overlapped) =>
            {
                transferred = WritePipeTransferred ?? bufferLength;
                return WritePipeResult;
            },
            controlTransfer: (IntPtr interfaceHandle, WinUsbNative.WinUsbSetupPacket setupPacket, byte[] buffer, uint bufferLength, out uint transferred, IntPtr overlapped) =>
            {
                transferred = ControlTransferBytes ?? bufferLength;
                return ControlTransferResult;
            },
            getClassDevs: (ref SetupApiNative.NativeGuid classGuid, string? enumerator, IntPtr hwndParent, uint flags) =>
            {
                GetClassDevsCalls++;
                return GetClassDevsResult ? DeviceInfoSet : SetupApiNative.InvalidHandleValue;
            },
            enumDeviceInterfaces: (IntPtr deviceInfoSet, IntPtr deviceInfoData, ref SetupApiNative.NativeGuid interfaceClassGuid, uint memberIndex, ref SetupApiNative.SpDeviceInterfaceData deviceInterfaceData) =>
            {
                EnumCalls++;
                return EnumInterfacesResult;
            },
            getDeviceInterfaceDetail: (IntPtr deviceInfoSet, ref SetupApiNative.SpDeviceInterfaceData deviceInterfaceData, IntPtr detailBuffer, uint deviceInterfaceDetailDataSize, out uint required, IntPtr relatedDeviceInfoData) =>
            {
                if (detailBuffer == IntPtr.Zero)
                {
                    // The size query is EXPECTED to fail with
                    // ERROR_INSUFFICIENT_BUFFER — requiredSize is its answer.
                    SizeQueryCalls++;
                    required = RequiredSize;
                    return false;
                }

                PathQueryCalls++;
                CbSizeWritten = Marshal.ReadInt32(detailBuffer);
                required = RequiredSize;
                if (!PathQueryResult) return false;

                byte[] path = Encoding.Unicode.GetBytes(DevicePath + "\0");
                Marshal.Copy(path, 0, detailBuffer + 4, path.Length);
                return true;
            },
            destroyDeviceInfoList: _ =>
            {
                DestroyListCalls++;
                return true;
            },
            createFile: (string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile) =>
            {
                CreateFileCalls++;
                CreateFileFlags = flagsAndAttributes;
                return CreateFileResult ? DeviceHandle : SetupApiNative.InvalidHandleValue;
            },
            closeHandle: _ =>
            {
                CloseHandleCalls++;
                return true;
            });
    }

    [TestMethod]
    public void Open_SuccessPath_EnumThenOpenThenInitializeThenPipeTimeouts()
    {
        var script = new ApiScript();
        using var device = new WinUsbBulkDevice(script.ToApi());

        bool ok = device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid);

        Assert.IsTrue(ok);
        Assert.IsTrue(device.IsOpen);
        Assert.AreEqual(1, script.GetClassDevsCalls);
        Assert.AreEqual(1, script.EnumCalls);
        Assert.AreEqual(1, script.SizeQueryCalls);
        Assert.AreEqual(1, script.PathQueryCalls);
        Assert.AreEqual(1, script.CreateFileCalls);
        Assert.AreEqual(1, script.InitializeCalls);
        Assert.AreEqual(1, script.DestroyListCalls, "the device info list must be destroyed after the attempt");
        // FILE_FLAG_OVERLAPPED is REQUIRED: WinUsb_Initialize fails with
        // ERROR_INVALID_HANDLE on a handle not opened for overlapped I/O.
        Assert.AreNotEqual(0u, script.CreateFileFlags & SetupApiNative.FileFlagOverlapped);
        // Control pipe 1000ms, bulk OUT pipe 30000ms.
        Assert.AreEqual(2, script.PipePolicies.Count);
        Assert.AreEqual(((byte)0x00, 1000), script.PipePolicies[0]);
        Assert.AreEqual(((byte)0x01, 30000), script.PipePolicies[1]);
    }

    [TestMethod]
    public void Open_CbSizeTrap_WritesStructSizeNotRequiredSize()
    {
        var script = new ApiScript { RequiredSize = 160 };
        using var device = new WinUsbBulkDevice(script.ToApi());

        bool ok = device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid);

        Assert.IsTrue(ok);
        // The ERROR_INVALID_USER_BUFFER (1784) trap: cbSize must be
        // sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W) — 8 on x64, 6 on x86 —
        // NOT the queried requiredSize (160), which the driver rejects.
        Assert.AreEqual(
            Marshal.SizeOf<SetupApiNative.SpDeviceInterfaceDetailData>(),
            script.CbSizeWritten,
            "cbSize must be the struct size, never the queried buffer size");
    }

    [TestMethod]
    public void Open_InitializeFails_ClosesDeviceHandleAndReturnsFalse()
    {
        var script = new ApiScript { InitializeResult = false };
        using var device = new WinUsbBulkDevice(script.ToApi());

        bool ok = device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid);

        Assert.IsFalse(ok);
        Assert.IsFalse(device.IsOpen);
        Assert.AreEqual(1, script.CloseHandleCalls, "the device handle must be closed when WinUsb_Initialize fails");
        Assert.AreEqual(0, script.PipePolicies.Count, "no pipe timeouts may be configured after a failed initialize");
    }

    [TestMethod]
    public void Open_GetClassDevsFails_ReturnsFalseWithoutFurtherCalls()
    {
        var script = new ApiScript { GetClassDevsResult = false };
        using var device = new WinUsbBulkDevice(script.ToApi());

        bool ok = device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, script.EnumCalls);
        Assert.AreEqual(0, script.CreateFileCalls);
        Assert.AreEqual(0, script.CloseHandleCalls);
        Assert.AreEqual(0, script.DestroyListCalls, "no device info list was created to destroy");
    }

    [TestMethod]
    public void Open_PathQueryFails_ReturnsFalseWithoutLeavingHandles()
    {
        var script = new ApiScript { PathQueryResult = false };
        using var device = new WinUsbBulkDevice(script.ToApi());

        bool ok = device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, script.CreateFileCalls, "no device handle exists before the path query");
        Assert.AreEqual(0, script.CloseHandleCalls);
    }

    [TestMethod]
    public void Open_EmptyDevicePath_ReturnsFalse()
    {
        var script = new ApiScript { DevicePath = "" };
        using var device = new WinUsbBulkDevice(script.ToApi());

        bool ok = device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, script.CreateFileCalls, "an empty path must never reach CreateFileW");
    }

    [TestMethod]
    public void Open_CreateFileFails_ReturnsFalseWithoutInitialize()
    {
        var script = new ApiScript { CreateFileResult = false };
        using var device = new WinUsbBulkDevice(script.ToApi());

        bool ok = device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid);

        Assert.IsFalse(ok);
        Assert.AreEqual(0, script.InitializeCalls);
        Assert.AreEqual(0, script.CloseHandleCalls, "the invalid handle must not be closed");
    }

    [TestMethod]
    public void Open_SetPipePolicyThrows_ReleasesPartialHandles()
    {
        // An exception after WinUsb_Initialize leaves both handles live.
        // Open's own catch must release them, so it never returns false with
        // the device still open — idempotent with the leg's Dispose on the
        // false return (the handles are zeroed, so the using-dispose is a
        // no-op and the counts stay at one each).
        var script = new ApiScript { SetPipePolicyThrows = true };
        using var device = new WinUsbBulkDevice(script.ToApi());

        bool ok = device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid);

        Assert.IsFalse(ok);
        Assert.AreEqual(1, script.FreeCalls, "the WinUSB interface handle must be freed on the exception path");
        Assert.AreEqual(1, script.CloseHandleCalls, "the device handle must be closed on the exception path");
    }

    // ── transfer wiring through the delegate bag ─────────────────────

    [TestMethod]
    public void ControlIn_ZeroByteTransfer_Fails()
    {
        // A zero-byte control-in reads as failure — the PING depends on the
        // WinUSB and LibUsb backends agreeing on transferred > 0.
        var script = new ApiScript { ControlTransferBytes = 0 };
        using var device = new WinUsbBulkDevice(script.ToApi());
        Assert.IsTrue(device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid));

        Assert.IsFalse(device.ControlIn(0x00, new byte[4], out _));
    }

    [TestMethod]
    public void ControlIn_FullTransfer_Succeeds()
    {
        var script = new ApiScript();
        using var device = new WinUsbBulkDevice(script.ToApi());
        Assert.IsTrue(device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid));

        Assert.IsTrue(device.ControlIn(0x00, new byte[4], out _));
    }

    [TestMethod]
    public void BulkWrite_ShortWrite_FailsAndReportsTransferred()
    {
        var script = new ApiScript { WritePipeTransferred = 123 };
        using var device = new WinUsbBulkDevice(script.ToApi());
        Assert.IsTrue(device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid));

        bool ok = device.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, new byte[1024], out int transferred);

        Assert.IsFalse(ok, "a short write is a failed write — the caller routes it to CmdFrameAbort");
        Assert.AreEqual(123, transferred);
    }

    [TestMethod]
    public void BulkWrite_FullTransfer_Succeeds()
    {
        var script = new ApiScript();
        using var device = new WinUsbBulkDevice(script.ToApi());
        Assert.IsTrue(device.Open(DisplayProtocolConstants.WinUsbInterfaceGuid));

        bool ok = device.BulkWrite(DisplayProtocolConstants.BulkOutPipeId, new byte[1024], out int transferred);

        Assert.IsTrue(ok);
        Assert.AreEqual(1024, transferred);
    }
}
