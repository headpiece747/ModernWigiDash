using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// Drives the transport's connect/init/frame/touch policy through the
/// <see cref="ITransferBackend"/> seam (the shared <see cref="RecordingBackend"/>
/// in TestDoubles.cs) — no hardware, no USB. The backend records every control
/// and bulk transfer, so the protocol framing the transport owns (init
/// sequence, frame header, touch parsing) is asserted exactly.
/// </summary>
[TestClass]
public class DisplayHidTransportTests
{
    /// <summary>
    /// Subclass seam over <see cref="WinUsbBulkDevice"/>: replaces the real
    /// SetupAPI/WinUSB P/Invoke surface with canned results and call counts,
    /// so <see cref="DisplayHidTransport.Connect"/>'s WinUSB policy (open →
    /// PING → init → LibUsb fallback) is drivable without hardware. Injected
    /// through a fake WinUSB <see cref="ConnectProvider"/> in
    /// <see cref="DisplayHidTransport.ProviderFactories"/>.
    /// </summary>
    private sealed class FakeWinUsbBulkDevice : WinUsbBulkDevice
    {
        public bool OpenResult { get; init; } = true;
        public bool ControlResult { get; init; } = true;
        public int OpenCalls { get; private set; }
        public int ControlInCalls { get; private set; }
        public int ControlOutCalls { get; private set; }
        public int BulkWriteCalls { get; private set; }
        public bool Disposed { get; private set; }

        private bool _isOpen;

        public override bool IsOpen => _isOpen;

        public override bool Open(Guid interfaceGuid)
        {
            OpenCalls++;
            _isOpen = OpenResult;
            return OpenResult;
        }

        public override bool ControlIn(byte request, byte[] buffer, out int transferred, ushort wValue = 0, ushort wIndex = 0)
        {
            ControlInCalls++;
            transferred = buffer.Length;
            return ControlResult;
        }

        public override bool ControlOut(byte request, ushort wValue, byte[]? data)
        {
            ControlOutCalls++;
            return ControlResult;
        }

        public override bool BulkWrite(byte pipeId, byte[] data, out int transferred)
        {
            BulkWriteCalls++;
            transferred = data.Length;
            return ControlResult;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Disposed = true;
                _isOpen = false;
            }
        }
    }

    /// <summary>A fake WinUSB provider in the image of the real leg's
    /// <c>TryCreateWinUsbBackend</c>: opens the fake device and returns it, or
    /// disposes it and returns null on a failed open — so the connect policy
    /// sees the same open/fail contract it would from the real provider.</summary>
    private static ConnectProvider WinUsbLeg(FakeWinUsbBulkDevice fake) => new(
        "USB-WINUSB",
        () =>
        {
            if (!fake.Open(DisplayProtocolConstants.WinUsbInterfaceGuid))
            {
                fake.Dispose();
                return null;
            }
            return fake;
        },
        "WinUSB");

    [TestMethod]
    public void Connect_WinUsbOpenAndPingSucceed_ConnectsAndRunsInit()
    {
        var fake = new FakeWinUsbBulkDevice();
        using var transport = new DisplayHidTransport();
        transport.ProviderFactories = [WinUsbLeg(fake)];

        bool ok = transport.Connect();

        Assert.IsTrue(ok);
        Assert.IsTrue(transport.IsConnected);
        Assert.AreEqual(1, fake.OpenCalls);
        // Exactly one PING in the whole connect: SendInitCommands owns it (the
        // pre-PING that used to run in Connect duplicated it and had drifted —
        // the provider loop opens the device and hands it over without PING).
        Assert.AreEqual(1, fake.ControlInCalls);
        // Init sequence: brightness + 3x (ClearPage + AddWidget) + FrameHeader + GoToScreen
        Assert.AreEqual(9, fake.ControlOutCalls);
        Assert.AreEqual(1, fake.BulkWriteCalls); // blank framebuffer
    }

    [TestMethod]
    public void Connect_WinUsbPingFails_FallsBackToLibUsb()
    {
        var fake = new FakeWinUsbBulkDevice { ControlResult = false };
        using var transport = new DisplayHidTransport();
        transport.ProviderFactories =
        [
            WinUsbLeg(fake),
            new ConnectProvider("USB-LIBUSB", () => null, "LibUsbDotNet 3.0"),
        ];

        // The WinUSB path must be abandoned after the failed PING (inside
        // SendInitCommands — the only PING in the connect, so the fake sees
        // exactly one ControlIn); the LibUsb fallback then reports no device,
        // so the outcome is deterministic. Assert the deterministic part: the
        // fake was consulted and disposed.
        bool ok = transport.Connect();

        Assert.AreEqual(1, fake.ControlInCalls, "The failed PING went through the injected fake");
        Assert.IsTrue(fake.Disposed, "The failed WinUSB device must be disposed");
        Assert.AreEqual(ok, transport.IsConnected, "Connection state must reflect the connect result");
    }

    // ── the provider loop: WinUSB → LibUsb fallback, drivable end-to-end ──

    /// <summary>
    /// The real WinUSB leg with the LibUsb leg replaced by a fake provider —
    /// the fallback is deterministic, no real hardware involved.
    /// <see cref="DisplayHidTransport.ProviderFactories"/> is the seam that
    /// makes both legs drivable.
    /// </summary>
    [TestMethod]
    public void Connect_WinUsbFailsToOpen_FakeLibUsbProviderConnects()
    {
        var winUsb = new FakeWinUsbBulkDevice { OpenResult = false };
        var libUsb = new RecordingBackend();
        using var transport = new DisplayHidTransport();
        transport.ProviderFactories =
        [
            WinUsbLeg(winUsb),
            new ConnectProvider("USB-LIBUSB", () => libUsb, "LibUsbDotNet 3.0"),
        ];

        bool ok = transport.Connect();

        Assert.IsTrue(ok);
        Assert.IsTrue(transport.IsConnected);
        Assert.AreEqual(1, winUsb.OpenCalls, "WinUSB is attempted first");
        Assert.IsTrue(winUsb.Disposed, "The failed WinUSB device must be disposed by its provider");
        // The fallback backend was adopted and received the init sequence: the
        // PING control-in is the first call.
        Assert.AreEqual("in", libUsb.ControlCalls[0].Direction);
        Assert.AreEqual(0x00, libUsb.ControlCalls[0].Request);
    }

    [TestMethod]
    public void Connect_WinUsbInitFails_FakeLibUsbProviderConnects()
    {
        var winUsb = new FakeWinUsbBulkDevice { ControlResult = false };
        var libUsb = new RecordingBackend();
        using var transport = new DisplayHidTransport();
        transport.ProviderFactories =
        [
            WinUsbLeg(winUsb),
            new ConnectProvider("USB-LIBUSB", () => libUsb, "LibUsbDotNet 3.0"),
        ];

        bool ok = transport.Connect();

        Assert.IsTrue(ok);
        Assert.IsTrue(transport.IsConnected);
        Assert.IsTrue(winUsb.Disposed, "A backend whose init failed must be disposed before the fallback");
        // The single PING (inside SendInitCommands) failed on the WinUSB
        // stack — no pre-PING ran in Connect.
        Assert.AreEqual(1, winUsb.ControlInCalls);
    }

    [TestMethod]
    public void Connect_AllProvidersFail_NotConnected()
    {
        var winUsb = new FakeWinUsbBulkDevice { OpenResult = false };
        using var transport = new DisplayHidTransport();
        transport.ProviderFactories =
        [
            WinUsbLeg(winUsb),
            new ConnectProvider("USB-LIBUSB", () => null, "LibUsbDotNet 3.0"),
        ];

        bool ok = transport.Connect();

        Assert.IsFalse(ok);
        Assert.IsFalse(transport.IsConnected);
        Assert.IsTrue(winUsb.Disposed, "Every provider's partial state must be torn down");
    }

    [TestMethod]
    public void Connect_ProvidersAreTriedInOrder_WinUsbFirstThenLibUsb()
    {
        List<string> order = [];
        var libUsb = new RecordingBackend();
        using var transport = new DisplayHidTransport();
        transport.ProviderFactories =
        [
new ConnectProvider("USB-WINUSB", () => { order.Add("winusb"); return null; }, "WinUSB"),
             new ConnectProvider("USB-LIBUSB", () => { order.Add("libusb"); return libUsb; }, "LibUsbDotNet 3.0"),
        ];

        bool ok = transport.Connect();

        Assert.IsTrue(ok);
        CollectionAssert.AreEqual(new[] { "winusb", "libusb" }, order, "The provider list is tried strictly in order");
    }

    [TestMethod]
    public void SendInitCommands_RunsFullInitSequence()
    {
        var backend = new RecordingBackend();
        using var transport = new DisplayHidTransport(backend);

        bool ok = transport.SendInitCommands();

        Assert.IsTrue(ok);
        // PING control-in, then per page: ClearPage (0x90), AddWidget (0x91)
        Assert.AreEqual("in", backend.ControlCalls[0].Direction);
        Assert.AreEqual(0x00, backend.ControlCalls[0].Request);
        for (int page = 0; page < 3; page++)
        {
            Assert.IsTrue(backend.ControlCalls.Any(c => c is { Direction: "out", Request: DisplayProtocolConstants.CmdClearPage } && c.WValue == page));
            Assert.IsTrue(backend.ControlCalls.Any(c => c is { Direction: "out", Request: DisplayProtocolConstants.CmdAddWidget } && c.WValue == (ushort)((page << 8) | 0)));
        }
        // Blank framebuffer bulk write
        Assert.AreEqual(1, backend.BulkWrites.Count);
        Assert.AreEqual(DisplayGeometry.FrameBufferSize, backend.BulkWrites[0].Length);
        // GoToScreen(Base0)
        Assert.IsTrue(backend.ControlCalls.Any(c => c is { Direction: "out", Request: DisplayProtocolConstants.CmdGoToScreen }));
    }

    [TestMethod]
    public void SendFrame_WritesHeaderThenBulkPayload()
    {
        var backend = new RecordingBackend();
        using var transport = new DisplayHidTransport(backend);
        byte[] frame = new byte[DisplayGeometry.FrameBufferSize];

        bool ok = transport.SendFrame(frame);

        Assert.IsTrue(ok);
        var header = backend.ControlCalls.Single(c => c.Request == DisplayProtocolConstants.CmdFrameHeader);
        Assert.AreEqual("out", header.Direction);
        Assert.AreEqual(0, header.WValue); // page 0, widget 0
        Assert.AreEqual(1, backend.BulkWrites.Count);
        Assert.AreSame(frame, backend.BulkWrites[0]);
    }

    [TestMethod]
    public void SendFrame_WhenBulkFails_SendsFrameAbortAndCountsFailure()
    {
        var backend = new RecordingBackend { BulkWriteResult = false };
        using var transport = new DisplayHidTransport(backend);
        byte[] frame = new byte[DisplayGeometry.FrameBufferSize];

        bool ok = transport.SendFrame(frame);

        Assert.IsFalse(ok);
        Assert.AreEqual(1, transport.FramesFailed);
        Assert.IsTrue(backend.ControlCalls.Any(c => c.Request == DisplayProtocolConstants.CmdFrameAbort));
    }

    [TestMethod]
    public void SendFrame_ShortBulkWrite_SendsFrameAbortAndCountsFailure()
    {
        // A backend reporting transferred < length fails the write — the same
        // full-transfer contract as the real backends — so the transport must
        // treat it like any bulk failure.
        var backend = new RecordingBackend { BulkWriteTransferred = 123 };
        using var transport = new DisplayHidTransport(backend);
        byte[] frame = new byte[DisplayGeometry.FrameBufferSize];

        bool ok = transport.SendFrame(frame);

        Assert.IsFalse(ok);
        Assert.AreEqual(1, transport.FramesFailed);
        Assert.IsTrue(backend.ControlCalls.Any(c => c.Request == DisplayProtocolConstants.CmdFrameAbort));
    }

    [TestMethod]
    public void SendFrame_TooSmallBuffer_ReturnsFalse()
    {
        var backend = new RecordingBackend();
        using var transport = new DisplayHidTransport(backend);

        Assert.IsFalse(transport.SendFrame(new byte[8]));
        Assert.AreEqual(0, backend.ControlCalls.Count);
    }

    [TestMethod]
    public void GoToStandby_SendsWelcomeScreenCommand()
    {
        var backend = new RecordingBackend();
        using var transport = new DisplayHidTransport(backend);

        bool ok = transport.GoToStandby();

        Assert.IsTrue(ok);
        // Standby = the vendor Welcome screen (wValue = screenId, no transition).
        Assert.IsTrue(backend.ControlCalls.Any(c => c is { Direction: "out", Request: DisplayProtocolConstants.CmdGoToScreen, WValue: DisplayProtocolConstants.ScreenWelcome }));
        // GoToScreen is only ever the standby path — page nav is compositor-side.
        Assert.IsTrue(backend.ControlCalls.Count(c => c.Request == DisplayProtocolConstants.CmdGoToScreen) == 1);
    }

    [TestMethod]
    public void GoToStandby_WhenDisconnected_ReturnsFalse()
    {
        var backend = new RecordingBackend { IsOpen = false };
        using var transport = new DisplayHidTransport(backend);

        Assert.IsFalse(transport.GoToStandby());
        Assert.AreEqual(0, backend.ControlCalls.Count);
    }

    [TestMethod]
    public void ReadTouch_ValidReport_ParsesCoordinatesAndType()
    {
        var backend = new RecordingBackend
        {
            // type=Down(1), x=470, y=322, screenState=0, sleepState=0
            TouchResponse = [1, 0, 0xD6, 0x01, 0x42, 0x01, 0, 0]
        };
        using var transport = new DisplayHidTransport(backend);

        TouchReport? report = transport.ReadTouch();

        Assert.IsNotNull(report);
        Assert.AreEqual(DisplayProtocolConstants.TouchTypeDown, report.Value.Type);
        Assert.AreEqual(470, report.Value.X);
        Assert.AreEqual(322, report.Value.Y);
    }

    [TestMethod]
    public void ReadTouch_NoneType_ReturnsNull()
    {
        var backend = new RecordingBackend
        {
            TouchResponse = [DisplayProtocolConstants.TouchTypeNone, 0, 0, 0, 0, 0, 0, 0]
        };
        using var transport = new DisplayHidTransport(backend);

        Assert.IsNull(transport.ReadTouch());
    }

    [TestMethod]
    public void ReadTouch_OutOfBoundsCoordinates_ReturnsNull()
    {
        var backend = new RecordingBackend
        {
            // x = 9999 (0x270F) — outside the 1016 px framebuffer
            TouchResponse = [1, 0, 0x0F, 0x27, 0x42, 0x01, 0, 0]
        };
        using var transport = new DisplayHidTransport(backend);

        Assert.IsNull(transport.ReadTouch());
    }

    [TestMethod]
    public void ReadTouch_ShortTransfer_ReturnsNull()
    {
        // A 2-byte transfer leaves the rest of the reused buffer stale — only
        // a full report may be parsed as a touch (the backend reports the
        // transferred count, and the transport requires all of TouchReportSize).
        var backend = new RecordingBackend
        {
            TouchResponse = [1, 0]
        };
        using var transport = new DisplayHidTransport(backend);

        Assert.IsNull(transport.ReadTouch());
    }

    [TestMethod]
    public void ReadTouch_WhenBackendClosed_ReturnsNull()
    {
        var backend = new RecordingBackend { IsOpen = false };
        using var transport = new DisplayHidTransport(backend);

        Assert.IsNull(transport.ReadTouch());
    }

    [TestMethod]
    public void Dispose_DisposesBackend()
    {
        var backend = new RecordingBackend();
        var transport = new DisplayHidTransport(backend);

        transport.Dispose();

        Assert.IsFalse(backend.IsOpen);
    }

    // ── LibUsb leg: teardown through the real provider + the device seam ──

    private static DisplayHidTransport TransportWithLibUsbDevice(FakeLibUsbDevice device)
    {
        var transport = new DisplayHidTransport();
        transport.LibUsbDeviceProvider = () => device;
        transport.ProviderFactories = [transport.LibUsbProvider];
        return transport;
    }

    [TestMethod]
    public void BuildFrameHeader_WritesLittleEndianOffsetAndLength()
    {
        // The wire format is owned once (the cold blank-framebuffer path and
        // the 30 FPS send path share BuildFrameHeader) — pin the layout here.
        byte[] header = new byte[DisplayProtocolConstants.FrameHeaderDataSize];

        DisplayHidTransport.BuildFrameHeader(header, 0x01020304);

        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 0x04, 0x03, 0x02, 0x01 }, header,
            "the header is [offset(4 LE), length(4 LE)] with a zero offset");
    }

    [TestMethod]
    public void Connect_LibUsbOpenThrows_ClosesTheDevice()
    {
        // The open/config/claimed local must be released on the terminal
        // exception path — the old catch disposed the adopted global backend
        // (null on the first attempt) and leaked this device until process exit.
        var device = new FakeLibUsbDevice { OpenThrows = true };
        using var transport = TransportWithLibUsbDevice(device);

        bool ok = transport.Connect();

        Assert.IsFalse(ok);
        Assert.AreEqual(1, device.CloseCalls, "the device whose open threw must be released, not leaked");
    }

    [TestMethod]
    public void Connect_LibUsbClaimFails_ClosesTheDevice()
    {
        var device = new FakeLibUsbDevice { ClaimResult = false };
        using var transport = TransportWithLibUsbDevice(device);

        bool ok = transport.Connect();

        Assert.IsFalse(ok);
        Assert.AreEqual(1, device.CloseCalls, "an unclaimed device must be released");
    }

    [TestMethod]
    public void Connect_LibUsbEndpointWriterThrows_ClosesTheDevice()
    {
        // The endpoint writer is opened inside the backend construction — a
        // throw leaves the open+claimed device orphaned unless the terminal
        // catch releases it.
        var device = new FakeLibUsbDevice { WriterThrows = true };
        using var transport = TransportWithLibUsbDevice(device);

        bool ok = transport.Connect();

        Assert.IsFalse(ok);
        Assert.AreEqual(1, device.CloseCalls, "an endpoint-writer failure must release the open+claimed device");
    }
}
