using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// Drives the transport's connect/init/frame/touch policy through the
/// <see cref="ITransferBackend"/> seam — no hardware, no USB. The backend
/// records every control and bulk transfer, so the protocol framing the
/// transport owns (init sequence, frame header, touch parsing) is asserted
/// exactly.
/// </summary>
[TestClass]
public class DisplayHidTransportTests
{
    private sealed record ControlCall(string Direction, byte Request, ushort WValue);

    private sealed class RecordingBackend : ITransferBackend
    {
        public List<ControlCall> ControlCalls { get; } = [];
        public List<byte[]> BulkWrites { get; } = [];
        public bool IsOpen { get; set; } = true;
        public bool ControlOutResult { get; set; } = true;
        public bool ControlInResult { get; set; } = true;
        public bool BulkWriteResult { get; set; } = true;
        public byte[]? TouchResponse { get; set; }

        public bool ControlOut(byte request, ushort wValue, byte[]? data)
        {
            ControlCalls.Add(new ControlCall("out", request, wValue));
            return ControlOutResult;
        }

        public bool ControlIn(byte request, byte[] buffer, ushort wValue = 0, ushort wIndex = 0)
        {
            ControlCalls.Add(new ControlCall("in", request, wValue));
            if (TouchResponse is not null && request == DisplayProtocolConstants.CmdGetTouch)
                TouchResponse.CopyTo(buffer, 0);
            return ControlInResult;
        }

        public bool BulkWrite(byte pipeId, byte[] data, out int transferred)
        {
            BulkWrites.Add(data);
            transferred = data.Length;
            return BulkWriteResult;
        }

        public void Dispose() => IsOpen = false;
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
    public void SendFrame_TooSmallBuffer_ReturnsFalse()
    {
        var backend = new RecordingBackend();
        using var transport = new DisplayHidTransport(backend);

        Assert.IsFalse(transport.SendFrame(new byte[8]));
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
}
