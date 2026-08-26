// <copyright file="DisplayDeviceEngine.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using SkiaSharp;
using System.Runtime.InteropServices;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Unified hardware engine for the USB display device.
/// Uses DisplayHidTransport for all USB communication - no vendor DLL dependencies.
/// All state is instance-owned: each engine owns its transport and connection
/// lifecycle. Callers that need one device per process create exactly
/// one engine (MainWindow does).
///
/// Frame delivery (encode → pool → coalesce → paced send) does NOT live here:
/// the App binds a <see cref="FrameDelivery"/> instance to
/// <see cref="SendFrameBytes"/> and the engine only owns connection, standby,
/// and touch. One delivery policy, every transport.
/// </summary>
public sealed class DisplayDeviceEngine : IDisposable
{
    // -- Connection State --
    private readonly Func<IDisplayTransport> _transportFactory;
    private IDisplayTransport? _transport;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private bool _connecting; // Prevent concurrent connection attempts
    private readonly Lock _lock = new();

    // -- Lifecycle State --
    private int _isDisposed;
    private readonly Timer _reconnectTimer;

    // The engine's file-log vocabulary: one DiagLog per connect area, each
    // binding its tag once at construction (the DiagLog module) so a category
    // can never drift between the engine's call sites.
    private readonly DiagLog _tryConnectLog = new("TryConnect", 1);
    private readonly DiagLog _connectLog = new("Connect", 1);
    private readonly DiagLog _reconnectLog = new("Reconnect", 1);
    private readonly DiagLog _disposeLog = new("Dispose", 1);
    // The standby verdict joins the area vocabulary: the 2026-08-21 on-device
    // incident made the NOT-confirmed lines load-bearing, and the tag must
    // match the transport's own STANDBY lines (its GoToStandby writes through
    // a bound DiagLog of the same tag) — one spelling of the verdict tag.
    private readonly DiagLog _standbyLog = new("STANDBY", 1);

    /// <summary>
    /// Test seam: the reconnect timer period (see <see cref="Start"/>).
    /// Defaults to 5s — the reconnect cadence on a missing/vanished device.
    /// </summary>
    internal TimeSpan ReconnectPeriod { get; set; } = TimeSpan.FromSeconds(5);

    // The never-stall-on-close budgets: read from the transport's close-budget
    // policy (DisplayHidTransport.CloseBudgets), where they are derived
    // strictly shorter than the worst-case teardown, so the relation holds by
    // construction instead of by a cross-file pin. A healthy close completes
    // in well under a second, so the bounds only ever bite for a hung device
    // (a leaked handle at exit beats a frozen window).
    internal static TimeSpan StandbyCloseBudget => DisplayHidTransport.CloseBudgets.StandbyCloseBudget;
    internal static TimeSpan DisposeAbandonBudget => DisplayHidTransport.CloseBudgets.DisposeAbandonBudget;

    // Direct-USB touch polling: the engine owns the transport, reads the touch
    // report at a 16ms cadence, and normalizes it once via
    // TouchReport.ToEventType. Idle while not connected (simulation mode) —
    // the direct-USB loop is the only touch owner.
    private readonly PollLoop _touchPoll;

    // -- Public State --
    /// <summary>The single connection truth — see <see cref="ConnectionState"/>.</summary>
    public ConnectionState State { get => _state; private set => _state = value; }

    /// <summary>
    /// The frame-readiness policy: delivery may push only while the engine is
    /// Connected. The predicate lives here — with the connection truth it
    /// describes — not at the app's bind site, so a readiness change (e.g.
    /// buffering during Connecting) edits one owner.
    /// </summary>
    public bool CanSendFrames => State == ConnectionState.Connected;

    // -- Events --
    /// <summary>
    /// Raised for each normalized hardware touch report. Fired from the
    /// engine's 16ms touch-poll background thread (a <see cref="PollLoop"/>
    /// tick), never the UI thread — handlers must marshal (e.g. via
    /// Dispatcher) before touching WPF state. See <see cref="SimulateTouch"/>
    /// for the test-driven counterpart.
    /// </summary>
    public event Action<SKPoint, TouchEventType>? OnTouchEvent;

    /// <summary>
    /// Creates the engine. The constructor is deliberately inert: no connect
    /// attempt, no background loops — construction must never reach for
    /// hardware (window field initializers, test hosts). Call <see cref="Start"/>
    /// to begin connection and touch polling. The transport is constructed
    /// lazily per connect attempt by the default factory (the real hardware
    /// transport), so the connect state machine stays drivable by the internal
    /// factory ctor's fake-transport seam in tests.
    /// </summary>
    public DisplayDeviceEngine() : this((Func<IDisplayTransport>?)null)
    {
    }

    /// <summary>
    /// Creates the engine. The transport is constructed lazily per connect
    /// attempt via <paramref name="transportFactory"/> (defaults to the real
    /// hardware transport), so the connect state machine is drivable with a
    /// fake transport end-to-end.
    /// </summary>
    internal DisplayDeviceEngine(Func<IDisplayTransport>? transportFactory)
    {
        _transportFactory = transportFactory ?? (() => new DisplayHidTransport());
        Log("=== Display Hardware Engine Initializing ===");

        // Direct-USB touch polling: active only while the engine owns the
        // device (connected, not simulation). One loop module, every hop.
        _touchPoll = CreateTouchPollLoop();

        // Reconnect timer is created but disarmed until Start().
        _reconnectTimer = new Timer(ReconnectTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Test seam: an engine bound to an injected transport, without auto-connect
    /// or background loops. The touch poll loop is created but not started —
    /// tests drive <see cref="TouchPollTick"/> directly (or call
    /// <see cref="Start"/> to exercise the loop wiring). The initial state is
    /// seeded by the caller through <paramref name="initialState"/> — the
    /// engine's ConnectionState is the one truth; the transport's own
    /// connection state is only meaningful after a Connect the engine
    /// observed. The transport
    /// factory returns this same transport, so a Start() that reaches
    /// TryConnect reconnects through it instead of NRE-ing.
    /// </summary>
    internal DisplayDeviceEngine(IDisplayTransport transport, ConnectionState initialState = ConnectionState.Disconnected)
    {
        _transport = transport;
        _transportFactory = () => transport;
        State = initialState;
        _touchPoll = CreateTouchPollLoop();
        _reconnectTimer = new Timer(ReconnectTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Starts the engine's background work: the 16ms touch poll, the initial
    /// connection attempt, and the reconnect timer (period:
    /// <see cref="ReconnectPeriod"/>). Called once by the window after
    /// construction; calling it again is harmless (the poll loop, the
    /// connection gate, and the timer are each guarded) but not required.
    /// </summary>
    public void Start()
    {
        if (Volatile.Read(ref _isDisposed) != 0) return;

        _touchPoll.Start();

        // The transport is deliberately synchronous (ADR-0001), so the initial
        // connect (WinUSB probe + init, ~100-150ms) must run off the calling
        // thread — Start() is invoked from the window ctor on the UI thread.
        // ReconnectTick below already runs on the Timer thread.
        RunConnectAttempt(_connectLog, "Initial connection failed, will retry via reconnect timer");

        _reconnectTimer.Change(ReconnectPeriod, ReconnectPeriod);
    }

#pragma warning disable S1172 // timer callback signature requires the state parameter
    /// <summary>
    /// One reconnect-timer tick: reconnects when the engine is alive, not
    /// connected, and no connect attempt is in flight. Internal so tests can
    /// drive the gate directly (the <see cref="Start"/>-armed timer is the
    /// production caller).
    /// </summary>
    internal void ReconnectTick(object? _)
    {
        bool shouldReconnect;
        lock (_lock)
        {
            shouldReconnect = Volatile.Read(ref _isDisposed) == 0
                && State != ConnectionState.Connected
                && !_connecting;
        }
        if (!shouldReconnect) return;

        RunConnectAttempt(_reconnectLog);
    }
#pragma warning restore S1172

    /// <summary>One fire-and-forget connect attempt with fault logging — the
    /// Start and reconnect-timer scaffolding share this shape (the connect
    /// itself is synchronous per ADR-0001, so it runs off the caller's
    /// thread). The caller passes the area's bound <paramref name="faultLog"/>
    /// (Connect for the initial attempt, Reconnect for the timer), so the
    /// fault and failure lines carry the area's tag instead of a hand-baked
    /// prefix.</summary>
    private void RunConnectAttempt(DiagLog faultLog, string? failureMessage = null)
        => _ = Task.Run(() => TryConnect()).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                faultLog.Write($"attempt faulted: {t.Exception?.GetBaseException().Message}");
            }
            else if (failureMessage is not null && !t.GetAwaiter().GetResult())
            {
                faultLog.Write(failureMessage);
            }
        }, TaskContinuationOptions.ExecuteSynchronously);

    private PollLoop CreateTouchPollLoop() => new(
        "TOUCH-DIRECT",
        TimeSpan.FromMilliseconds(16),
        ready: () => State == ConnectionState.Connected,
        tick: TouchPollTick,
        onTickFailure: () => Log("Touch poll tick failed"),
        log: msg => Log(msg));

    /// <summary>
    /// One direct-USB touch probe: reads the transport's pending report and
    /// raises <see cref="OnTouchEvent"/> with the SDK-normalized type. This is
    /// the App-side transport seam — vendor protocol bytes never leave it.
    /// </summary>
    internal void TouchPollTick()
    {
        // Snapshot under the lock: the connect thread swaps/disposes the
        // transport (SendFrameBytes's pattern) — a lock-free read could probe
        // a transport mid-dispose.
        IDisplayTransport? transport;
        lock (_lock)
        {
            transport = _transport;
        }

        if (transport?.ReadTouch() is not TouchReport report)
            return;

        OnTouchEvent?.Invoke(new SKPoint(report.X, report.Y), TouchReport.ToEventType(report.Type));
    }

    /// <summary>
    /// Attempts to connect to the physical device. Deliberately synchronous
    /// (ADR-0001) — callers run it off the UI thread (see <see cref="Start"/>).
    /// Guards against concurrent connection attempts to prevent connection churn.
    /// Internal: the only production driver is the engine's own connect state
    /// machine (Start, the reconnect timer, the power-resume hop) — no app
    /// call site pokes it directly.
    /// </summary>
    internal bool TryConnect()
    {
        // Fast-path: already connected
        if (State == ConnectionState.Connected)
        {
            _tryConnectLog.Write("Already connected, skipping");
            return true;
        }

        // Guard against concurrent connection attempts
        lock (_lock)
        {
            if (_connecting || Volatile.Read(ref _isDisposed) != 0)
            {
                _tryConnectLog.Write($"Connection in progress or disposed, skipping (state={State})");
                return State == ConnectionState.Connected;
            }
            _connecting = true;
        }

        try
        {
            State = ConnectionState.Connecting;

            // Disconnect any existing transport before attempting new connection
            DisconnectInternal();

            IDisplayTransport? transport = null;
            bool connected = false;
            bool disposedDuringConnect = false;

            try
            {
                transport = _transportFactory();
                connected = transport.Connect();
            }
            catch (Exception ex)
            {
                _connectLog.Write($"Connection exception: {ex.Message}");
                connected = false;
            }

            if (connected)
            {
                // Dispose() can run while this connect is in flight (the
                // entry guard predates the assignment). Re-check under the
                // lock: a disposed engine must not adopt a live transport —
                // it would never reach standby and the handle would leak.
                lock (_lock)
                {
                    disposedDuringConnect = Volatile.Read(ref _isDisposed) != 0;
                    if (!disposedDuringConnect)
                    {
                        _transport = transport;
                    }
                }

                if (disposedDuringConnect)
                {
                    DisposeTransport(transport);
                    connected = false;
                }
                else
                {
                    // Connect() is the single init owner: it already ran
                    // SendInitCommands (PING + SetBrightness + ClearPage +
                    // AddWidget + blank framebuffer + GoToScreen) on both the
                    // WinUSB and LibUsb paths.
                    State = ConnectionState.Connected;
                    Log("Hardware connection successful!");
                }
            }
            else
            {
                // The un-adopted transport is released whether Connect()
                // returned false or the attempt threw — the orphan rule a
                // transport that would never reach standby must not leak its
                // handle (the production transport self-cleans on a false
                // return; Dispose is idempotent for it, and the rule holds for
                // any future transport that allocates on connect).
                DisposeTransport(transport);
                Log("Transport connection failed - falling back to simulation");
            }

            if (!connected && !disposedDuringConnect)
            {
                lock (_lock)
                {
                    _transport = null;
                }
                State = ConnectionState.Simulated;
                Log("No physical device found - running in simulation mode");
            }

            return connected;
        }
        finally
        {
            lock (_lock)
            {
                _connecting = false;
            }
        }
    }

    /// <summary>Releases an un-adopted transport (the orphan rule): a
    /// transport the engine never took ownership of must be disposed — it
    /// would never reach standby and its handle would leak. Safe on null.</summary>
    private static void DisposeTransport(IDisplayTransport? transport)
    {
        transport?.Dispose();
    }

    /// <summary>
    /// Sends an already-encoded RGB565 frame to the device. The frame delivery
    /// policy (pooling, coalescing, pacing) lives in <see cref="FrameDelivery"/>;
    /// this is the engine's plain transport seam. Takes a
    /// <see cref="ReadOnlyMemory{T}"/> so the send chain (delivery seam,
    /// engine, transport) shares one buffer type with zero copies at the
    /// boundaries.
    /// </summary>
    /// <returns>The transport's truthful send result — <see cref="Sdk.FrameSendResult.Refused"/>
    /// when the engine cannot carry the frame (disposed, no live connection, no
    /// adopted transport, or an unbacked buffer — the null shape of the
    /// ReadOnlyMemory seam is a <see cref="ReadOnlyMemory{T}"/> with no backing
    /// array). The SIZE contract belongs to the
    /// transport, which alone knows <c>DisplayProtocolConstants.FrameBufferSize</c>
    /// — the engine forwards what it is handed and relays the transport's
    /// verdict, so the size rule has one owner.</returns>
    public FrameSendResult SendFrameBytes(ReadOnlyMemory<byte> rgb565)
    {
        // An unbacked memory (the default/null shape) has no array; a backed
        // but empty buffer is still a frame the transport's size contract
        // adjudicates — the engine filters only "no buffer at all".
        if (Volatile.Read(ref _isDisposed) != 0 || State != ConnectionState.Connected || !MemoryMarshal.TryGetArray(rgb565, out _))
            return FrameSendResult.Refused;

        IDisplayTransport? transport;
        lock (_lock)
        {
            transport = _transport;
        }

        if (transport == null)
            return FrameSendResult.Refused;

        return transport.SendFrame(rgb565);
    }

    /// <summary>
    /// Simulates a touch event for testing (internal test seam — production
    /// touch flows through the 16ms poll loop).
    /// </summary>
    internal void SimulateTouch(float x, float y, TouchEventType eventType)
    {
        OnTouchEvent?.Invoke(new SKPoint(x, y), eventType);
    }

    /// <summary>
    /// Disconnects from the device and cleans up resources.
    /// </summary>
    private void DisconnectInternal()
    {
        IDisplayTransport? oldTransport;
        lock (_lock)
        {
            State = ConnectionState.Disconnected;
            oldTransport = _transport;
            _transport = null;
        }

        // Dispose outside the lock to avoid holding it during I/O — and bounded
        // off-thread: an in-flight frame write holds the transport's lock for
        // up to the transport's CloseBound (DisplayHidTransport.CloseBound —
        // the backend's worst-case stall budget) on a hung device, and close
        // must never stall on that (the standby pattern — a leaked handle at
        // exit beats a frozen window); DisposeAbandonBudget is the abandon
        // point, derived shorter than CloseBound. The timer thread may
        // also reach this via the reconnect path, where an abandoned dispose
        // is equally fine.
        if (oldTransport != null)
        {
            try
            {
                Task.Run(() => oldTransport.Dispose()).Wait(DisposeAbandonBudget);
            }
            catch (Exception ex)
            {
                _disposeLog.Write($"Transport disposal failed or timed out: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Writes a log message to the log file.
    /// </summary>
    private static void Log(string msg) => FileLog.Write(msg);

    /// <summary>
    /// The non-disposing standby: the dispose path's bounded-wait,
    /// verdict-reading sequence for a caller that wants the display in its
    /// vendor sleep state WITHOUT tearing the engine down. The session-end
    /// path is the production caller: a system shutdown while the display is
    /// live kills the process mid-frame-stream, the documented wedge case
    /// (the display has no auto-sleep, stays lit, and the next connect can
    /// hang on the init blank-frame write until a physical unplug), so the
    /// standby ritual runs while the session ends. Returns the truthful
    /// verdict — true only when the transport confirmed the standby writes;
    /// false when there is no live transport (nothing to standby), the
    /// bounded wait expired, or the standby threw. Every non-confirmed
    /// verdict logs through the shared STANDBY vocabulary. Safe to call
    /// concurrently with <see cref="Dispose"/>: the transport's own
    /// connection gate degrades a mid-dispose call to a false verdict.
    /// </summary>
    public bool TryGoToStandby()
    {
        if (Volatile.Read(ref _isDisposed) != 0)
        {
            return false;
        }

        IDisplayTransport? transport;
        lock (_lock)
        {
            transport = _transport;
        }
        if (transport is null)
        {
            // No device (never connected, or removed): nothing to put to
            // standby — the expected no-device state, not a failure.
            return false;
        }

        // The dispose path's sequence, verbatim: off-thread with the bounded
        // StandbyCloseBudget wait (a hung device holds the control transfer
        // out to its pipe timeout), and the verdict read from the task's
        // Result only once Wait confirms completion (the 2026-08-21 rule:
        // Wait(timeout) reports completion, not the result).
        bool confirmed = false;
        bool settled = false;
        bool threw = false;
        try
        {
            var standbyTask = Task.Run(() => transport.GoToStandby());
            settled = standbyTask.Wait(StandbyCloseBudget);
            if (settled)
            {
                confirmed = standbyTask.Result;
            }
        }
        catch (Exception ex)
        {
            threw = true;
            _standbyLog.Write($"Standby failed: {ex.Message}");
        }
        // The non-confirmed verdict is observable, the dispose path's rule:
        // a display left lit on the Welcome screen is a fact the log must
        // carry (a silent standby attempt would hide it).
        if (!confirmed && !threw)
        {
            _standbyLog.Write(settled
                ? "Standby NOT confirmed: the standby control writes did not succeed — the display may stay lit"
                : "Standby NOT confirmed: the bounded wait expired — a hung standby was abandoned, and the display may stay lit");
        }
        return confirmed;
    }

    /// <summary>
    /// Releases all resources used by the engine.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0) return;

        _touchPoll.Dispose();
        _reconnectTimer.Dispose();

        // Direct-USB mode owns the device, so the app is responsible for putting
        // the display into standby when it exits (the standby path's sleep command
        // turns the backlight off). When the transport is null (no device
        // attached), there is nothing to put to standby.
        IDisplayTransport? transport;
        lock (_lock)
        {
            transport = _transport;
        }

        // Off-thread with a bounded wait: a hung device can hold a control
        // transfer out to its pipe timeout (worst case the transport's
        // CloseBound), and standby is exactly such a control transfer.
        // StandbyCloseBudget is the abandon point, derived strictly shorter
        // than CloseBound, so close can never stall on a hung pipe — a
        // leaked handle at exit beats a frozen window. (The old rationale
        // — waiting out an in-flight frame write behind the transport's
        // coarse lock — retired with that lock: the hot paths no longer
        // serialize, and the transport's teardown drains in-flight
        // transfers before freeing the backend.)
        bool standbyConfirmed = false;
        bool standbySettled = false;
        bool standbyThrew = false;
        try
        {
            var standbyTask = Task.Run(() => transport?.GoToStandby() ?? false);
            // Wait(timeout) reports only that the task finished inside the
            // budget; the device's verdict rides the task's RESULT, readable
            // only once completion is known — on the abandon path the hung
            // standby is left to run out (a leaked handle at exit beats a
            // frozen window).
            standbySettled = standbyTask.Wait(StandbyCloseBudget);
            if (standbySettled)
            {
                standbyConfirmed = standbyTask.Result;
            }
        }
        catch (Exception ex)
        {
            standbyThrew = true;
            _standbyLog.Write($"Standby failed during dispose: {ex.Message}");
        }
        // The standby verdict is observable: a standby that did not confirm
        // (no live connection, the control writes failed, or the bounded
        // wait expired) must not go silent — the display may stay lit on
        // the Welcome screen, and that is a fact the log must carry.
        if (transport is not null && !standbyConfirmed && !standbyThrew)
        {
            _standbyLog.Write(standbySettled
                ? "Standby NOT confirmed during dispose: the standby control writes did not succeed — the display may stay lit"
                : "Standby NOT confirmed during dispose: the bounded close wait expired — a hung standby was abandoned at exit, and the display may stay lit");
        }

        DisconnectInternal();
    }
}
