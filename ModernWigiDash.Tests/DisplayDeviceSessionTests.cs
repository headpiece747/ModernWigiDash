using System.Diagnostics;
using System.IO;
using ModernWigiDash.Hardware.Transport;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class DisplayDeviceSessionTests
{
    private string _logDir = "";
    private string _logPath = "";

    [TestInitialize]
    public void Init()
    {
        _logDir = Path.Combine(Path.GetTempPath(), $"wmd-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_logDir);
        _logPath = Path.Combine(_logDir, "display_device.log");
        FileLog.LogPath = _logPath;
    }

    [TestCleanup]
    public void Cleanup()
    {
        FileLog.LogPath = Path.Combine(AppContext.BaseDirectory, "display_device.log");
        try { Directory.Delete(_logDir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [TestMethod]
    public void Constructed_WithoutStart_StaysDisconnected()
    {
        using var session = new DisplayDeviceSession();
        Assert.AreEqual(ConnectionState.Disconnected, session.State);
        Assert.IsFalse(session.CanSendFrames);
    }

    [TestMethod]
    public void Start_WithInjectedTransport_BeginsTouchPolling()
    {
        var fake = new FakeTransport
        {
            ConnectResult = true,
            ConnectedAfterConnect = true,
            NextReport = new TouchReport { Type = DisplayProtocolConstants.TouchTypeDown, X = 100, Y = 200 },
        };

        SKPoint? seenPoint = null;
        TouchEventType? seenType = null;
        using var session = new DisplayDeviceSession(() => fake);
        session.OnTouchEvent += (pt, type) =>
        {
            seenPoint = pt;
            seenType = type;
        };

        session.Start();
        bool connected = SpinWait.SpinUntil(() => session.State == ConnectionState.Connected, TimeSpan.FromSeconds(2));
        Assert.IsTrue(connected, "Start should trigger connection");

        bool received = SpinWait.SpinUntil(() => seenPoint is not null, TimeSpan.FromSeconds(2));
        Assert.IsTrue(received, "Start should begin touch polling and receive touch event");
        Assert.AreEqual(new SKPoint(100, 200), seenPoint);
        Assert.AreEqual(TouchEventType.TouchDown, seenType);
    }

    [TestMethod]
    public void TouchPollTick_WithDownReport_RaisesOnTouchEventNormalized()
    {
        var fake = new FakeTransport
        {
            NextReport = new TouchReport { Type = DisplayProtocolConstants.TouchTypeDown, X = 320, Y = 240 },
        };

        using var session = new DisplayDeviceSession(fake, ConnectionState.Connected);
        SKPoint? capturedPoint = null;
        TouchEventType? capturedType = null;

        session.OnTouchEvent += (pt, type) =>
        {
            capturedPoint = pt;
            capturedType = type;
        };

        session.TouchPollTick();

        Assert.IsNotNull(capturedPoint);
        Assert.AreEqual(new SKPoint(320, 240), capturedPoint.Value);
        Assert.AreEqual(TouchEventType.TouchDown, capturedType);
    }

    [TestMethod]
    public void TouchPollTick_WithUpReport_RaisesTouchUp()
    {
        var fake = new FakeTransport
        {
            NextReport = new TouchReport { Type = DisplayProtocolConstants.TouchTypeUp, X = 150, Y = 75 },
        };

        using var session = new DisplayDeviceSession(fake, ConnectionState.Connected);
        TouchEventType? capturedType = null;

        session.OnTouchEvent += (_, type) => capturedType = type;
        session.TouchPollTick();

        Assert.AreEqual(TouchEventType.TouchUp, capturedType);
    }

    [TestMethod]
    public void TouchPollTick_NoPendingReport_RaisesNothing()
    {
        var fake = new FakeTransport { NextReport = null };
        using var session = new DisplayDeviceSession(fake, ConnectionState.Connected);

        bool fired = false;
        session.OnTouchEvent += (_, _) => fired = true;
        session.TouchPollTick();

        Assert.IsFalse(fired);
    }

    [TestMethod]
    public void SimulateTouch_RaisesOnTouchEventWithCoordinates()
    {
        using var session = new DisplayDeviceSession();
        SKPoint? point = null;
        TouchEventType? type = null;

        session.OnTouchEvent += (p, t) =>
        {
            point = p;
            type = t;
        };

        session.SimulateTouch(50f, 60f, TouchEventType.TouchMove);

        Assert.AreEqual(new SKPoint(50f, 60f), point);
        Assert.AreEqual(TouchEventType.TouchMove, type);
    }

    [TestMethod]
    public void SendFrameBytes_WhenDisconnected_ReturnsRefused()
    {
        using var session = new DisplayDeviceSession();
        byte[] buffer = new byte[DisplayGeometry.FrameBufferSize];
        var result = session.SendFrameBytes(buffer);

        Assert.AreEqual(FrameSendResult.Refused, result);
    }

    [TestMethod]
    public void SendFrameBytes_WhenConnected_ForwardsToTransport()
    {
        var fake = new FakeTransport
        {
            ConnectResult = true,
            ConnectedAfterConnect = true,
        };

        using var session = new DisplayDeviceSession(fake, ConnectionState.Connected);
        byte[] buffer = new byte[DisplayGeometry.FrameBufferSize];
        var result = session.SendFrameBytes(buffer);

        Assert.AreEqual(FrameSendResult.Sent, result);
        Assert.AreEqual(DisplayGeometry.FrameBufferSize, fake.LastFrameLength);
    }

    [TestMethod]
    public void TryConnect_ConnectSucceeds_AdoptsTransportAndSetsConnected()
    {
        var fake = new FakeTransport
        {
            ConnectResult = true,
            ConnectedAfterConnect = true,
        };

        using var session = new DisplayDeviceSession(() => fake);
        bool ok = session.TryConnect();

        Assert.IsTrue(ok);
        Assert.AreEqual(ConnectionState.Connected, session.State);
        Assert.IsTrue(session.CanSendFrames);
    }

    [TestMethod]
    public void TryConnect_ConnectFails_SetsFailureState()
    {
        var fake = new FakeTransport
        {
            ConnectResult = false,
            ConnectedAfterConnect = false,
            ConnectOutcome = ConnectionState.DeviceBusy,
        };

        using var session = new DisplayDeviceSession(() => fake);
        bool ok = session.TryConnect();

        Assert.IsFalse(ok);
        Assert.AreEqual(ConnectionState.DeviceBusy, session.State);
        Assert.IsFalse(session.CanSendFrames);
    }

    [TestMethod]
    public void TryGoToStandby_WhenDisconnected_ReturnsFalse()
    {
        using var session = new DisplayDeviceSession();
        bool result = session.TryGoToStandby();

        Assert.IsFalse(result);
    }

    [TestMethod]
    public void TryGoToStandby_WhenConnected_CallsTransportStandby()
    {
        var fake = new FakeTransport
        {
            GoToStandbyResult = true,
        };

        using var session = new DisplayDeviceSession(fake, ConnectionState.Connected);
        bool result = session.TryGoToStandby();

        Assert.IsTrue(result);
    }

    [TestMethod]
    public void Dispose_Twice_IsIdempotentAndSafe()
    {
        var fake = new FakeTransport();
        var session = new DisplayDeviceSession(fake, ConnectionState.Connected);

        session.Dispose();
        session.Dispose();

        Assert.AreEqual(ConnectionState.Disconnected, session.State);
    }
}
