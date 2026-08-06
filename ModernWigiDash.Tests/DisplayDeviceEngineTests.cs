using ModernWigiDash.Hardware;
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
        Assert.AreEqual(DisplayProtocolConstants.FramebufferWidth, DisplayDeviceEngine.ScreenWidth);
        Assert.AreEqual(DisplayProtocolConstants.FramebufferHeight, DisplayDeviceEngine.ScreenHeight);
        Assert.AreEqual(DisplayProtocolConstants.FrameBufferSize, DisplayDeviceEngine.FrameBufferSize);
    }

    [TestMethod]
    public void NewEngine_DefaultsToSimulationModeNotConnected()
    {
        using var engine = new DisplayDeviceEngine();

        Assert.IsTrue(engine.IsSimulationMode);
        Assert.IsFalse(engine.IsHardwareActive);
        Assert.IsFalse(engine.IsConnected);
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
    public void SendFrameBuffer_WhenDisconnected_IsNoOp()
    {
        using var engine = new DisplayDeviceEngine();
        using var bitmap = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul);

        // Must not throw and must not report activity; engine is in simulation mode.
        engine.SendFrameBuffer(bitmap);

        Assert.IsFalse(engine.IsHardwareActive);
    }

    [TestMethod]
    public void Dispose_Twice_IsSafe()
    {
        var engine = new DisplayDeviceEngine();
        engine.Dispose();
        // Second dispose must not throw.
        engine.Dispose();
    }
}
