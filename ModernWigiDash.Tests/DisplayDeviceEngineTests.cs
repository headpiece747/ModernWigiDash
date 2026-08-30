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
        Assert.AreEqual(FrameSendResult.Refused, engine.SendFrameBytes(Array.Empty<byte>()));
        // The unbacked buffer (the null shape for the ReadOnlyMemory seam —
        // the engine's guard refuses a memory with no backing array):
        Assert.AreEqual(FrameSendResult.Refused, engine.SendFrameBytes(default));
    }

    [TestMethod]
    public void SendFrameBytes_WhenConnected_ForwardsTheSizeVerdictToTheTransport()
    {
        // The engine owns liveness; the transport owns the size contract
        // (FrameBufferSize — pinned in DisplayHidTransportTests' too-small
        // refusal). A live engine must forward even an undersized buffer and
        // relay the transport's verdict — filtering by size here would
        // re-derive the protocol constant in a second module.
        var fake = new FakeTransport { ConnectResult = true, ConnectedAfterConnect = true };
        using var engine = new DisplayDeviceEngine(() => fake);
        Assert.IsTrue(engine.TryConnect());

        Assert.AreEqual(FrameSendResult.Sent, engine.SendFrameBytes(Array.Empty<byte>()),
            "a live engine relays the transport's verdict, even for a buffer the device would refuse");
        Assert.AreEqual(0, fake.LastFrameLength, "the engine must not swallow the buffer — the transport sees exactly what was handed in");
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
    public void Dispose_WhenStandbyThrows_LogsTheFailureVerdict()
    {
        // A standby that throws (a device that errors mid-ritual) lands the
        // failure line through the engine's one bounded-wait routine — the
        // raw exception never escapes Dispose.
        var fake = new FakeTransport { GoToStandbyFailure = "control write refused mid-ritual" };
        var engine = new DisplayDeviceEngine(fake);

        engine.Dispose();
        FileLog.Flush();

        string content = ReadLog(_logPath);
        Assert.IsTrue(content.Contains("[STANDBY] Standby failed during dispose"),
            "a throwing standby must leave its tagged failure line in the log");
        Assert.IsTrue(content.Contains("control write refused mid-ritual"),
            "the failure line must carry the device's error");
        Assert.IsTrue(fake.Disposed, "a throwing standby must not stop the teardown from disposing the transport");
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

    // ── the non-disposing standby (the session-end path) ────────────

    [TestMethod]
    public void TryGoToStandby_WhenConfirmed_ReturnsTrueAndLeavesTheEngineAlive()
    {
        // The session-end path: the display reaches the vendor sleep state
        // and the engine stays up (not disposed, connection intact) — the
        // caller runs the teardown or is killed right after.
        var fake = new FakeTransport { GoToStandbyResult = true };
        using var engine = new DisplayDeviceEngine(fake, ConnectionState.Connected);

        bool confirmed = engine.TryGoToStandby();

        Assert.IsTrue(confirmed, "the transport's confirmed verdict must propagate");
        Assert.IsFalse(fake.Disposed, "the standby must not dispose the transport");
        Assert.AreEqual(ConnectionState.Connected, engine.State, "the standby must not tear down the connection state");
    }

    [TestMethod]
    public void TryGoToStandby_WhenNotConfirmed_LogsTheVerdict()
    {
        // The dispose path's rule: a standby that did not confirm must leave
        // its tagged verdict line — a silent attempt would hide a display
        // left lit on the Welcome screen.
        var fake = new FakeTransport(); // GoToStandbyResult defaults to false
        using var engine = new DisplayDeviceEngine(fake);

        bool confirmed = engine.TryGoToStandby();
        FileLog.Flush();

        Assert.IsFalse(confirmed, "the transport's false verdict must propagate");
        string content = ReadLog(_logPath);
        Assert.IsTrue(content.Contains("[STANDBY] Standby NOT confirmed"),
            "an unconfirmed standby must leave its tagged verdict line, the dispose path's rule");
    }

    [TestMethod]
    public void TryGoToStandby_WhenHangsPastTheBudget_AbandonsAndLogsTheVerdict()
    {
        // The wedged-pipe scenario: a standby that wedges past
        // StandbyCloseBudget must not freeze the caller — the bounded wait
        // abandons it, and the log names the abandon path.
        var fake = new FakeTransport { GoToStandbyBlockMs = 3000 };
        using var engine = new DisplayDeviceEngine(fake);

        var stopwatch = Stopwatch.StartNew();
        bool confirmed = engine.TryGoToStandby();
        stopwatch.Stop();
        FileLog.Flush();

        Assert.IsFalse(confirmed);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"the standby must stay inside the bounded budget, took {stopwatch.Elapsed.TotalSeconds:0.0} s");
        string content = ReadLog(_logPath);
        Assert.IsTrue(content.Contains("bounded wait expired"),
            "the verdict must name the abandon path (the budget expired, not a failed write)");
    }

    [TestMethod]
    public void TryGoToStandby_WhenStandbyThrows_LogsTheFailureVerdictAndReturnsFalse()
    {
        // A standby that throws lands the failure line through the engine's
        // one bounded-wait routine (the same sequence the dispose path runs)
        // and refuses the standby — the raw exception never escapes.
        var fake = new FakeTransport { GoToStandbyFailure = "control write refused mid-ritual" };
        using var engine = new DisplayDeviceEngine(fake);

        bool confirmed = engine.TryGoToStandby();
        FileLog.Flush();

        Assert.IsFalse(confirmed, "a throwing standby is a refusal, not a confirmation");
        string content = ReadLog(_logPath);
        Assert.IsTrue(content.Contains("[STANDBY] Standby failed"),
            "a throwing standby must leave its tagged failure line in the log");
        Assert.IsTrue(content.Contains("control write refused mid-ritual"),
            "the failure line must carry the device's error");
        Assert.IsFalse(fake.Disposed, "the session-end standby must not dispose the transport");
    }

    [TestMethod]
    public void TryGoToStandby_WhenNoTransport_ReturnsFalseSilently()
    {
        // Production ctor, never started: no device — the expected no-device
        // state, and no verdict line, the no-device dispose's rule.
        var engine = new DisplayDeviceEngine();

        bool confirmed = engine.TryGoToStandby();
        FileLog.Flush();

        Assert.IsFalse(confirmed, "no device: nothing to put to standby");
        string content = ReadLog(_logPath);
        Assert.IsFalse(content.Contains("Standby NOT confirmed"), "a no-device standby is not a standby failure");
    }

    [TestMethod]
    public void TryGoToStandby_WhenDisposed_ReturnsFalse()
    {
        // A disposed engine has no live transport and no business sending
        // control transfers — the liveness guard refuses before any work.
        var fake = new FakeTransport { GoToStandbyResult = true };
        var engine = new DisplayDeviceEngine(fake);
        engine.Dispose();

        Assert.IsFalse(engine.TryGoToStandby(), "a disposed engine must refuse the standby");
    }

    private static string ReadLog(string path)
    {
        if (!File.Exists(path)) return "";
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ── the connect state machine, driven through the factory seam ──

    [TestMethod]
    [DataRow(ConnectionState.Disconnected, false)]
    [DataRow(ConnectionState.Connecting, false)]
    [DataRow(ConnectionState.Connected, true)]
    [DataRow(ConnectionState.Simulated, false)]
    public void CanSendFrames_TracksTheConnectionState(ConnectionState state, bool expected)
    {
        // The readiness policy the delivery bind site reads: true only for
        // Connected, pinned per state so the engine and the bind site cannot
        // drift on what "ready" means.
        using var engine = new DisplayDeviceEngine(new FakeTransport(), state);
        Assert.AreEqual(expected, engine.CanSendFrames);
    }

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
}
