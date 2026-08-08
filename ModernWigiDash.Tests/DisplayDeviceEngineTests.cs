using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class DisplayDeviceEngineTests
{
    // ── Touch type normalization (TouchReport.ToEventType) ────────────

    [DataTestMethod]
    [DataRow(DisplayProtocolConstants.TouchTypeDown, TouchEventType.TouchDown)]
    [DataRow(DisplayProtocolConstants.TouchTypeUp, TouchEventType.TouchUp)]
    [DataRow(DisplayProtocolConstants.TouchTypeNone, TouchEventType.TouchMove)]
    [DataRow((byte)0xAA, TouchEventType.TouchMove)]
    public void ToEventType_RawVendorByte_MapsToSdkVocabulary(byte raw, TouchEventType expected)
    {
        Assert.AreEqual(expected, TouchReport.ToEventType(raw));
    }

    // ── Direct-USB touch polling (engine touch loop tick) ─────────────

    [TestMethod]
    public void TouchPollTick_WithDownReport_RaisesOnTouchEventNormalized()
    {
        var fake = new FakeTransport { NextReport = new TouchReport
        {
            Type = DisplayProtocolConstants.TouchTypeDown,
            X = 12,
            Y = 34,
            ScreenState = 0,
            SleepState = false
        } };
        using var engine = new DisplayDeviceEngine(fake);
        SKPoint? receivedPoint = null;
        TouchEventType? receivedType = null;
        engine.OnTouchEvent += (point, type) =>
        {
            receivedPoint = point;
            receivedType = type;
        };

        engine.TouchPollTick();

        Assert.IsNotNull(receivedPoint);
        Assert.AreEqual(12f, receivedPoint.Value.X);
        Assert.AreEqual(34f, receivedPoint.Value.Y);
        Assert.AreEqual(TouchEventType.TouchDown, receivedType);
    }

    [TestMethod]
    public void TouchPollTick_WithUpReport_RaisesTouchUp()
    {
        var fake = new FakeTransport { NextReport = new TouchReport
        {
            Type = DisplayProtocolConstants.TouchTypeUp,
            X = 5,
            Y = 6,
            ScreenState = 0,
            SleepState = false
        } };
        using var engine = new DisplayDeviceEngine(fake);
        TouchEventType? receivedType = null;
        engine.OnTouchEvent += (_, type) => receivedType = type;

        engine.TouchPollTick();

        Assert.AreEqual(TouchEventType.TouchUp, receivedType);
    }

    [TestMethod]
    public void TouchPollTick_NoPendingReport_RaisesNothing()
    {
        var fake = new FakeTransport { NextReport = null };
        using var engine = new DisplayDeviceEngine(fake);
        int raised = 0;
        engine.OnTouchEvent += (_, _) => raised++;

        engine.TouchPollTick();

        Assert.AreEqual(0, raised);
    }

    // ── Pre-existing engine tests ─────────────────────────────────────
    [TestMethod]
    public void Constants_MatchProtocolConstants()
    {
        // Regression guard: pin the hardware spec values (1016x592 framebuffer,
        // RGB565 = 2 bytes per pixel). MSTEST0032 is disabled because constant
        // pins are always-true by construction — they ARE the protocol contract.
#pragma warning disable MSTEST0032
        Assert.AreEqual(1016, DisplayDeviceEngine.ScreenWidth);
        Assert.AreEqual(592, DisplayDeviceEngine.ScreenHeight);
        Assert.AreEqual(1016 * 592 * 2, DisplayDeviceEngine.FrameBufferSize);
#pragma warning restore MSTEST0032
    }

    [TestMethod]
    public void NewEngine_ConstructsAndDisposesSafely()
    {
        // The constructor fires a fire-and-forget TryConnectAsync, so connection
        // state is intentionally not asserted: on a machine with the display
        // attached (or the service running) it legitimately connects.
        var engine = new DisplayDeviceEngine();
        engine.Dispose();

        // After dispose the engine must be inert: sends report failure, not throw.
        Assert.IsFalse(engine.SendFrameBytes(new byte[8]));
    }

    [TestMethod]
    public void SimulateTouch_RaisesOnTouchEventWithCoordinates()
    {
        using var engine = new DisplayDeviceEngine();
        SKPoint? receivedPoint = null;
        TouchEventType? receivedType = null;
        engine.OnTouchEvent += (point, type) =>
        {
            receivedPoint = point;
            receivedType = type;
        };

        engine.SimulateTouch(12.5f, 34.5f, TouchEventType.TouchDown);

        Assert.IsNotNull(receivedPoint);
        Assert.AreEqual(12.5f, receivedPoint.Value.X);
        Assert.AreEqual(34.5f, receivedPoint.Value.Y);
        Assert.AreEqual(TouchEventType.TouchDown, receivedType);
    }

    [TestMethod]
    public void SimulateTouch_ReleaseEventType_Raised()
    {
        using var engine = new DisplayDeviceEngine();
        TouchEventType? receivedType = null;
        engine.OnTouchEvent += (_, type) => receivedType = type;

        engine.SimulateTouch(1, 2, TouchEventType.TouchUp);

        Assert.AreEqual(TouchEventType.TouchUp, receivedType);
    }

    [TestMethod]
    public void SendFrameBytes_WhenDisconnected_IsNoOp()
    {
        using var engine = new DisplayDeviceEngine();

        // Must not throw and must report failure when the engine has no live connection.
        Assert.IsFalse(engine.SendFrameBytes(new byte[8]));
        Assert.IsFalse(engine.SendFrameBytes([]));
        Assert.IsFalse(engine.SendFrameBytes(null!));
    }

    [TestMethod]
    public void Dispose_Twice_IsSafe()
    {
        var engine = new DisplayDeviceEngine();
        engine.Dispose();
        // Second dispose must not throw.
        engine.Dispose();

        // The engine must stay inert after a second dispose — no throw, no send.
        Assert.IsFalse(engine.SendFrameBytes(new byte[8]));
    }

    /// <summary>
    /// Minimal <see cref="IDisplayTransport"/> fake: returns the canned
    /// <see cref="NextReport"/> from <see cref="ReadTouch"/>, never connects.
    /// </summary>
    private sealed class FakeTransport : IDisplayTransport
    {
        public TouchReport? NextReport { get; set; }

        public bool IsConnected => false;
        public string DevicePath => "fake";

        public bool Connect() => false;
        public void Disconnect() { }
        public bool SetBrightness(byte brightnessPercent) => false;
        public bool SendFrame(ReadOnlyMemory<byte> frameBuffer) => false;
        public bool GoToScreen(byte screenId, byte transition = 0) => false;
        public bool ClearPage(byte page = 0) => false;
        public bool ClearTimeout() => false;
        public TouchReport? ReadTouch() => NextReport;
        public bool SendInitCommands() => false;
        public bool GoToStandby() => false;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
