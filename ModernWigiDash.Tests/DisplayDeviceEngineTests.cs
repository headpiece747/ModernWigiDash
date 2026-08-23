using System.Diagnostics;
using System.IO;
using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

[TestClass]
public class DisplayDeviceEngineTests
{
    private string _logDir = "";
    private string _logPath = "";

    [TestInitialize]
    public void Init()
    {
        // The engine's Dispose logs the standby verdict through FileLog (a
        // shared static): redirect to a per-test temp file so the verdict
        // lines are assertable and the test output dir stays clean.
        _logDir = Path.Combine(Path.GetTempPath(), $"wmd-engine-{Guid.NewGuid():N}");
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
        using var engine = new DisplayDeviceEngine(fake, ConnectionState.Connected);
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

    // ── the standby verdict on dispose (observable in the display log) ──

    [TestMethod]
    public void Dispose_WhenStandbyNotConfirmed_LogsTheVerdict()
    {
        // The standby verdict is observable: a standby that did not confirm
        // (the control writes did not succeed) must land a line in the log —
        // a silent dispose would hide a display left lit on the Welcome screen.
        var fake = new FakeTransport(); // GoToStandbyResult defaults to false
        var engine = new DisplayDeviceEngine(fake);

        engine.Dispose();
        FileLog.Flush();

        string content = ReadLog(_logPath);
        Assert.IsTrue(content.Contains("[STANDBY] Standby NOT confirmed"),
            "an unconfirmed standby must leave its tagged verdict line in the log — the tag is the area's bound spelling, asserted with the text");
    }

    [TestMethod]
    public void Dispose_WhenStandbyHangsPastTheBudget_AbandonsAndLogsTheVerdict()
    {
        // The wedged-pipe scenario: a standby that wedges past
        // StandbyCloseBudget must not freeze the close — the bounded wait
        // abandons it, and the log carries the verdict (the display may
        // still be lit).
        var fake = new FakeTransport { GoToStandbyBlockMs = 3000 };
        var engine = new DisplayDeviceEngine(fake);

        var stopwatch = Stopwatch.StartNew();
        engine.Dispose();
        stopwatch.Stop();
        FileLog.Flush();

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"dispose must stay inside the bounded budgets (2 s standby + the fast fake dispose), took {stopwatch.Elapsed.TotalSeconds:0.0} s");
        string content = ReadLog(_logPath);
        Assert.IsTrue(content.Contains("[STANDBY] Standby NOT confirmed"),
            "an abandoned standby must leave its tagged verdict line in the log");
        Assert.IsTrue(content.Contains("bounded close wait expired"),
            "the verdict must name the abandon path (the budget expired, not a failed write)");
    }

    [TestMethod]
    public void Dispose_WhenStandbyConfirms_LogsNoFailureVerdict()
    {
        var fake = new FakeTransport { GoToStandbyResult = true };
        var engine = new DisplayDeviceEngine(fake);

        engine.Dispose();
        FileLog.Flush();

        string content = ReadLog(_logPath);
        Assert.IsFalse(content.Contains("Standby NOT confirmed"), "a confirmed standby leaves no failure verdict");
        Assert.IsFalse(content.Contains("Standby failed"), "a confirmed standby leaves no failure line");
    }

    [TestMethod]
    public void Dispose_WhenNoDevice_LogsNoStandbyVerdict()
    {
        // Production ctor, never started: no device, no transport — nothing to
        // put to standby, and no verdict line either: the absent standby
        // attempt is the expected no-device state, not a failure.
        var engine = new DisplayDeviceEngine();

        engine.Dispose();
        FileLog.Flush();

        string content = ReadLog(_logPath);
        Assert.IsFalse(content.Contains("Standby NOT confirmed"), "a no-device dispose is not a standby failure");
    }

    private static string ReadLog(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Minimal <see cref="IDisplayTransport"/> fake: returns the canned
    /// <see cref="NextReport"/> from <see cref="ReadTouch"/>, and answers the
    /// connect and standby outcomes per test (default: never connects, standby
    /// never confirms).
    /// </summary>
    private sealed class FakeTransport : IDisplayTransport
    {
        public TouchReport? NextReport { get; set; }
        public bool ConnectResult { get; set; }
        public bool ConnectedAfterConnect { get; set; }
        public bool GoToStandbyResult { get; set; }
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
        /// <summary>Simulates a standby that wedges past the engine's
        /// StandbyCloseBudget (the wedged bulk-pipe scenario).</summary>
        public int GoToStandbyBlockMs { get; set; }
        public bool GoToStandby()
        {
            if (GoToStandbyBlockMs > 0) Thread.Sleep(GoToStandbyBlockMs);
            return GoToStandbyResult;
        }
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
    public void InternalCtor_SeedsStateFromTheExplicitParameter()
    {
        // The test seam no longer asks the transport for its connection truth —
        // the engine's ConnectionState is the one truth, and a bound engine
        // starts in whatever state the caller says it is in.
        var connected = new DisplayDeviceEngine(new FakeTransport { ConnectResult = true }, ConnectionState.Connected);
        Assert.AreEqual(ConnectionState.Connected, connected.State, "a bound engine starts in the caller-seeded state");

        var simulated = new DisplayDeviceEngine(new FakeTransport { ConnectResult = false }, ConnectionState.Simulated);
        Assert.AreEqual(ConnectionState.Simulated, simulated.State);
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

    [TestMethod]
    public void CloseBudgets_AbandonBeforeWorstCaseTeardown()
    {
        // The engine's close waits are abandon points: each is deliberately
        // shorter than the transport's CloseBound (the worst-case time a hung
        // device can hold the teardown lock) — a leaked handle at exit beats a
        // frozen window. If a budget ever reached CloseBound, close would
        // follow a hung device to the very end and stall on it. The invariant
        // is pinned on both sides of the seam (CloseBound itself is pinned in
        // DisplayHidTransportTests) so it can never drift silently.
        Assert.IsTrue(DisplayDeviceEngine.StandbyCloseBudget < DisplayHidTransport.CloseBound,
            "the standby close must abandon before a worst-case teardown");
        Assert.IsTrue(DisplayDeviceEngine.DisposeAbandonBudget < DisplayHidTransport.CloseBound,
            "the transport dispose must abandon before a worst-case teardown");
    }
}
