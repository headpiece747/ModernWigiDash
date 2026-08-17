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
    public void Constructed_WithoutStart_StaysDisconnected()
    {
        // The ctor is inert: no connect attempt, no background loops. The old
        // ctor probed real USB (and even put the
        // attached display into standby on dispose) from the test host.
        using var engine = new DisplayDeviceEngine();

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

        // After dispose the engine must be inert: sends are refused, not throw.
        Assert.AreEqual(FrameSendResult.Refused, engine.SendFrameBytes(new byte[8]));
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

        // Must not throw and must report refusal when the engine has no live connection.
        Assert.AreEqual(FrameSendResult.Refused, engine.SendFrameBytes(new byte[8]));
        Assert.AreEqual(FrameSendResult.Refused, engine.SendFrameBytes([]));
        Assert.AreEqual(FrameSendResult.Refused, engine.SendFrameBytes(null!));
    }

    [TestMethod]
    public void Dispose_Twice_IsSafe()
    {
        var engine = new DisplayDeviceEngine();
        engine.Dispose();
        // Second dispose must not throw.
        engine.Dispose();

        // The engine must stay inert after a second dispose — no throw, no send.
        Assert.AreEqual(FrameSendResult.Refused, engine.SendFrameBytes(new byte[8]));
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
        public FrameSendResult SendFrame(ReadOnlyMemory<byte> frameBuffer) => IsConnected ? FrameSendResult.Sent : FrameSendResult.Refused;
        public TouchReport? ReadTouch() => NextReport;
        public bool GoToStandby() => false;
        /// <summary>Simulates a device whose Dispose hangs behind an in-flight
        /// frame write (the bulk-write timeout path).</summary>
        public int DisposeBlockMs { get; set; }
        public void Dispose()
        {
            if (DisposeBlockMs > 0) Thread.Sleep(DisposeBlockMs);
            Disposed = true;
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    // ── the connect state machine, driven through the factory seam ──

    [TestMethod]
    public void TryConnect_ConnectSucceeds_AdoptsTransportAndConnects()
    {
        var fake = new FakeTransport { ConnectResult = true, ConnectedAfterConnect = true };
        using var engine = new DisplayDeviceEngine(() => fake);

        bool ok = engine.TryConnect();

        Assert.IsTrue(ok);
        Assert.AreEqual(ConnectionState.Connected, engine.State);
        Assert.AreEqual(FrameSendResult.Sent, engine.SendFrameBytes(new byte[8]), "the adopted transport must carry frames");
    }

    [TestMethod]
    public void TryConnect_ConnectFails_FallsBackToSimulated()
    {
        using var engine = new DisplayDeviceEngine(() => new FakeTransport { ConnectResult = false });

        bool ok = engine.TryConnect();

        Assert.IsFalse(ok);
        Assert.AreEqual(ConnectionState.Simulated, engine.State, "no device - running in simulation mode");
    }

    [TestMethod]
    public void TryConnect_ConnectFails_DisposesTheUnAdoptedTransport()
    {
        // The orphan rule: a transport the engine never adopted (Connect()
        // returned false) must be released — the disposed-during-connect
        // branch already enforces it, and a future transport that allocates
        // on a failed connect would otherwise leak its handle.
        var fake = new FakeTransport { ConnectResult = false };
        using var engine = new DisplayDeviceEngine(() => fake);

        bool ok = engine.TryConnect();

        Assert.IsFalse(ok);
        Assert.IsTrue(fake.Disposed, "a failed-connect transport must be released, never leaked");
    }

    [TestMethod]
    public void TryConnect_DisposedDuringConnect_DoesNotAdoptTransport()
    {
        var fake = new FakeTransport { ConnectResult = true, ConnectedAfterConnect = true };
        using var engine = new DisplayDeviceEngine(() => fake);
        fake.OnConnect = () => engine.Dispose();

        bool ok = engine.TryConnect();

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

    // ── the reconnect tick gate ──────────────────────────────────────

    [TestMethod]
    public void ReconnectTick_WhenDisposed_DoesNotReconnect()
    {
        int factoryCalls = 0;
        using var engine = new DisplayDeviceEngine(() => { factoryCalls++; return new FakeTransport(); });
        engine.Dispose();

        engine.ReconnectTick(null);

        Assert.AreEqual(0, factoryCalls, "a disposed engine must not attempt a reconnect");
    }

    [TestMethod]
    public void ReconnectTick_WhenConnected_DoesNotReconnect()
    {
        int factoryCalls = 0;
        var fake = new FakeTransport { ConnectResult = true, ConnectedAfterConnect = true };
        using var engine = new DisplayDeviceEngine(() => { factoryCalls++; return fake; });
        Assert.IsTrue(engine.TryConnect(), "precondition: the engine connects");
        Assert.AreEqual(1, factoryCalls);

        engine.ReconnectTick(null);

        Assert.AreEqual(1, factoryCalls, "a connected engine must not attempt a reconnect");
    }

    [TestMethod]
    public void ReconnectTick_WhenDisconnected_ReconnectsThroughFactory()
    {
        int factoryCalls = 0;
        using var reconnected = new ManualResetEventSlim(false);
        using var engine = new DisplayDeviceEngine(() =>
        {
            factoryCalls++;
            reconnected.Set();
            return new FakeTransport { ConnectResult = false };
        });

        engine.ReconnectTick(null);

        Assert.IsTrue(reconnected.Wait(2000), "the reconnect tick must drive a connect attempt through the factory");
        Assert.AreEqual(1, factoryCalls);
    }

    [TestMethod]
    public void ReconnectPeriod_DefaultsToFiveSeconds()
    {
        using var engine = new DisplayDeviceEngine();

        Assert.AreEqual(TimeSpan.FromSeconds(5), engine.ReconnectPeriod);
    }

    [TestMethod]
    public void Dispose_WithHungTransport_ReturnsWithinBound()
    {
        // A hung device holds the transport lock behind an in-flight frame
        // write (bulk-write timeout); close must not stall on it — the bounded
        // off-thread dispose (the standby pattern) caps the wait.
        var fake = new FakeTransport { DisposeBlockMs = 15_000 };
        var engine = new DisplayDeviceEngine(fake);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        engine.Dispose();
        sw.Stop();

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(8),
            $"engine Dispose must not stall behind a hung transport (took {sw.Elapsed.TotalSeconds:F1}s)");
        Assert.IsFalse(fake.Disposed, "the abandoned dispose may still be running off-thread");
    }
}
