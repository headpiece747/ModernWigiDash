using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class DisplayDeviceEngineTests
{
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
}
