using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class DisplayDeviceEngineTests
{
    // ── Touch type normalization (TouchReport.ToEventType) ────────────

    [TestMethod]
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
        var fake = new FakeTransport
        {
            NextReport = new TouchReport
            {
                Type = DisplayProtocolConstants.TouchTypeDown,
                X = 12,
                Y = 34
            }
        };
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
        var fake = new FakeTransport
        {
            NextReport = new TouchReport
            {
                Type = DisplayProtocolConstants.TouchTypeUp,
                X = 5,
                Y = 6
            }
        };
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
    public async Task Constructed_WithoutStart_StaysDisconnected()
    {
        // The ctor is inert: no connect attempt, no background loops. The old
        // ctor fired TryConnectAsync and probed real USB (and even put the
        // attached display into standby on dispose) from the test host.
        using var engine = new DisplayDeviceEngine();
        await Task.Delay(150); // generous — the old ctor's probe settled within ~50ms

        Assert.AreEqual(ConnectionState.Disconnected, engine.State);
    }

    [TestMethod]
    public void Start_WithInjectedTransport_BeginsTouchPolling()
    {
        var fake = new FakeTransport
        {
            ConnectResult = true,
            ConnectedAfterConnect = true,
            NextReport = new TouchReport
            {
                Type = DisplayProtocolConstants.TouchTypeDown,
                X = 12,
                Y = 34
            }
        };
        using var engine = new DisplayDeviceEngine(fake);
        using var received = new ManualResetEventSlim(false);
        engine.OnTouchEvent += (_, _) => received.Set();

        engine.Start();

        Assert.IsTrue(received.Wait(2000), "The 16ms touch poll must deliver the fake report after Start");
    }

    [TestMethod]
    public void NewEngine_ConstructsAndDisposesSafely()
    {
        // The ctor is inert (no connect attempt, no loops), so construction is
        // safe in any host; dispose must leave the engine inert too.
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
    /// <see cref="NextReport"/> from <see cref="ReadTouch"/>, and answers the
    /// connect outcome per test (default: never connects).
    /// </summary>
    private sealed class FakeTransport : IDisplayTransport
    {
        public TouchReport? NextReport { get; set; }
        public bool ConnectResult { get; set; }
        public bool ConnectedAfterConnect { get; set; }
        public bool Disposed { get; private set; }
        public Action? OnConnect { get; set; }

        public bool IsConnected => ConnectResult && ConnectedAfterConnect;

        public bool Connect()
        {
            OnConnect?.Invoke();
            return ConnectResult;
        }
        public bool SendFrame(ReadOnlyMemory<byte> frameBuffer) => IsConnected;
        public TouchReport? ReadTouch() => NextReport;
        public bool GoToStandby() => false;
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    // ── the connect state machine, driven through the factory seam ──

    [TestMethod]
    public void TryConnectAsync_ConnectSucceeds_AdoptsTransportAndConnects()
    {
        var fake = new FakeTransport { ConnectResult = true, ConnectedAfterConnect = true };
        using var engine = new DisplayDeviceEngine(() => fake);

        bool ok = engine.TryConnectAsync().GetAwaiter().GetResult();

        Assert.IsTrue(ok);
        Assert.AreEqual(ConnectionState.Connected, engine.State);
        Assert.IsTrue(engine.SendFrameBytes(new byte[8]), "the adopted transport must carry frames");
    }

    [TestMethod]
    public void TryConnectAsync_ConnectFails_FallsBackToSimulated()
    {
        using var engine = new DisplayDeviceEngine(() => new FakeTransport { ConnectResult = false });

        bool ok = engine.TryConnectAsync().GetAwaiter().GetResult();

        Assert.IsFalse(ok);
        Assert.AreEqual(ConnectionState.Simulated, engine.State, "no device - running in simulation mode");
    }

    [TestMethod]
    public void TryConnectAsync_DisposedDuringConnect_DoesNotAdoptTransport()
    {
        var fake = new FakeTransport { ConnectResult = true, ConnectedAfterConnect = true };
        using var engine = new DisplayDeviceEngine(() => fake);
        fake.OnConnect = () => engine.Dispose();

        bool ok = engine.TryConnectAsync().GetAwaiter().GetResult();

        Assert.IsFalse(ok, "a disposed engine must not adopt a live transport");
        Assert.IsTrue(fake.Disposed, "the orphan transport must be disposed, never leaked");
        Assert.AreNotEqual(ConnectionState.Connected, engine.State);
    }

    [TestMethod]
    public void InternalCtor_StateDerivesFromTransportConnectionTruth()
    {
        var connected = new DisplayDeviceEngine(new FakeTransport { ConnectResult = true, ConnectedAfterConnect = true });
        Assert.AreEqual(ConnectionState.Connected, connected.State, "an open transport reports Connected");

        var simulated = new DisplayDeviceEngine(new FakeTransport { ConnectResult = false });
        Assert.AreEqual(ConnectionState.Simulated, simulated.State, "a closed transport must not report Connected");
    }
}
