// <copyright file="DisplayDeviceEngine.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using ModernWigiDash.Sdk;
using SkiaSharp;

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

    /// <summary>
    /// Test seam: the reconnect timer period (see <see cref="Start"/>).
    /// Defaults to 5s — the reconnect cadence on a missing/vanished device.
    /// </summary>
    internal TimeSpan ReconnectPeriod { get; set; } = TimeSpan.FromSeconds(5);

    // Direct-USB touch polling: the engine owns the transport, reads the touch
    // report at a 16ms cadence, and normalizes it once via
    // TouchReport.ToEventType. Idle while not connected (simulation mode) —
    // the direct-USB loop is the only touch owner.
    private readonly PollLoop _touchPoll;

    // -- Public State --
    /// <summary>The single connection truth — see <see cref="ConnectionState"/>.</summary>
    public ConnectionState State { get => _state; private set => _state = value; }

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
    /// to begin connection and touch polling.
    /// </summary>
    /// <summary>
    /// Creates the engine. The transport is constructed lazily per connect
    /// attempt via <paramref name="transportFactory"/> (defaults to the real
    /// hardware transport), so the connect state machine is drivable with a
    /// fake transport end-to-end.
    /// </summary>
    public DisplayDeviceEngine(Func<IDisplayTransport>? transportFactory = null)
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
    /// <see cref="Start"/> to exercise the loop wiring). The state derives
    /// from the injected transport's actual connection truth — never asserted.
    /// The transport factory returns this same transport, so a Start() that
    /// reaches TryConnect reconnects through it instead of NRE-ing.
    /// </summary>
    internal DisplayDeviceEngine(IDisplayTransport transport)
    {
        _transport = transport;
        _transportFactory = () => transport;
        State = transport.IsConnected ? ConnectionState.Connected : ConnectionState.Simulated;
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
        _ = Task.Run(() => TryConnect()).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Log($"Initial connection faulted: {t.Exception?.GetBaseException().Message}");
            }
            else if (!t.Result)
            {
                Log("Initial connection failed, will retry via reconnect timer");
            }
        }, TaskContinuationOptions.ExecuteSynchronously);

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

        _ = Task.Run(() => TryConnect()).ContinueWith(t =>
        {
            if (t.IsFaulted)
                Log($"[Reconnect] Connection attempt faulted: {t.Exception?.GetBaseException().Message}");
        }, TaskContinuationOptions.ExecuteSynchronously);
    }
#pragma warning restore S1172

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
        if (_transport?.ReadTouch() is not TouchReport report)
            return;

        OnTouchEvent?.Invoke(new SKPoint(report.X, report.Y), TouchReport.ToEventType(report.Type));
    }

    /// <summary>
    /// Attempts to connect to the physical device. Deliberately synchronous
    /// (ADR-0001) — callers run it off the UI thread (see <see cref="Start"/>).
    /// Guards against concurrent connection attempts to prevent connection churn.
    /// </summary>
    public bool TryConnect()
    {
        // Fast-path: already connected
        if (State == ConnectionState.Connected)
        {
            Log("[TryConnect] Already connected, skipping");
            return true;
        }

        // Guard against concurrent connection attempts
        lock (_lock)
        {
            if (_connecting || Volatile.Read(ref _isDisposed) != 0)
            {
                Log($"[TryConnect] Connection in progress or disposed, skipping (state={State})");
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
#pragma warning disable S6966 // Sync dispose of an orphan transport; async not needed here
                        transport?.Dispose();
#pragma warning restore S6966
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
                    Log("Transport connection failed - falling back to simulation");
                }
            }
            catch (Exception ex)
            {
                Log($"[Connect] Connection exception: {ex.Message}");
#pragma warning disable S6966 // Cleanup transport in catch; no async dispose needed here
                transport?.Dispose();
#pragma warning restore S6966
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

    /// <summary>
    /// Sends an already-encoded RGB565 frame to the device. The frame delivery
    /// policy (pooling, coalescing, pacing) lives in <see cref="FrameDelivery"/>;
    /// this is the engine's plain transport seam.
    /// </summary>
    /// <returns>True when the frame was written to the transport.</returns>
    public bool SendFrameBytes(byte[] rgb565)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || State != ConnectionState.Connected || rgb565 == null || rgb565.Length == 0)
            return false;

        IDisplayTransport? transport;
        lock (_lock)
        {
            transport = _transport;
        }

        if (transport == null)
            return false;

#pragma warning disable S6966 // Transport SendFrame is synchronous by design (ADR-0001)
        return transport.SendFrame(rgb565);
#pragma warning restore S6966
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

        // Dispose outside lock to avoid holding lock during I/O
        if (oldTransport != null)
        {
            try
            {
                oldTransport.Dispose();
            }
            catch (Exception ex)
            {
                Log($"[Dispose] Transport disposal failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Writes a log message to the log file.
    /// </summary>
    private static void Log(string msg) => FileLog.Write(msg);

    /// <summary>
    /// Releases all resources used by the engine.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0) return;

        _touchPoll.Dispose();
        _reconnectTimer.Dispose();

        // Direct-USB mode owns the device, so the app is responsible for putting
        // the display into standby when it exits. When the transport is null
        // (no device attached), there is nothing to put to standby — the
        // display sleeps on its own timeout once heartbeats stop.
        try
        {
            // Off-thread with a bounded wait: close must not hang behind an
            // in-flight frame write holding the transport lock (the LibUsb
            // chunked write can block on chunk timeouts). Standby itself is a
            // fast control transfer once the lock frees; 2s bounds the worst
            // case, so close can never stall on the write.
            Task.Run(() => _transport?.GoToStandby()).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Log($"[STANDBY] Standby failed during dispose: {ex.Message}");
        }

        DisconnectInternal();
    }
}
