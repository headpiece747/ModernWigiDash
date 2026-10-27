// <copyright file="DisplayDeviceSession.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using SkiaSharp;
using System.Runtime.InteropServices;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Unified hardware session for the USB display device.
/// Encapsulates connection lifecycle, reconnect backoff, 16ms touch polling,
/// frame transmission, and bounded standby/teardown execution.
/// </summary>
public sealed class DisplayDeviceSession : IDisposable
{
    private readonly Func<IDisplayTransport> _transportFactory;
    private IDisplayTransport? _transport;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private bool _connecting;
    private readonly Lock _lock = new();

    private int _isDisposed;
    private readonly Timer _reconnectTimer;
    private readonly PollLoop _touchPoll;
    private int _consecutiveFailures;

    private readonly DiagLog _tryConnectLog = new("TryConnect", 1);
    private readonly DiagLog _connectLog = new("Connect", 1);
    private readonly DiagLog _reconnectLog = new("Reconnect", 1);
    private readonly DiagLog _disposeLog = new("Dispose", 1);
    private readonly DiagLog _standbyLog = new("STANDBY", 1);

    internal TimeSpan ReconnectPeriod { get; set; } = ReconnectBackoffPolicy.FirstDelay;

    private CloseBudgetPolicy EffectiveBudgets => _transport?.CloseBudgets ?? DisplayHidTransport.DefaultCloseBudgets;

    internal TimeSpan StandbyCloseBudget => EffectiveBudgets.StandbyCloseBudget;
    internal TimeSpan DisposeAbandonBudget => EffectiveBudgets.DisposeAbandonBudget;

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public ConnectionState State { get => _state; private set => _state = value; }

    /// <summary>
    /// Gets a value indicating whether frames can currently be dispatched.
    /// </summary>
    public bool CanSendFrames => State == ConnectionState.Connected;

    /// <summary>
    /// Raised when a normalized touch report is received.
    /// </summary>
    public event Action<SKPoint, TouchEventType>? OnTouchEvent;

    /// <summary>
    /// Initializes a new instance of the <see cref="DisplayDeviceSession"/> class.
    /// </summary>
    public DisplayDeviceSession() : this((Func<IDisplayTransport>?)null)
    {
    }

    internal DisplayDeviceSession(Func<IDisplayTransport>? transportFactory)
    {
        _transportFactory = transportFactory ?? (() => new DisplayHidTransport());
        FileLog.Write("=== Display Hardware Session Initializing ===");

        _touchPoll = CreateTouchPollLoop();
        _reconnectTimer = new Timer(ReconnectTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    internal DisplayDeviceSession(IDisplayTransport transport, ConnectionState initialState = ConnectionState.Disconnected)
    {
        _transport = transport;
        _transportFactory = () => transport;
        State = initialState;
        _touchPoll = CreateTouchPollLoop();
        _reconnectTimer = new Timer(ReconnectTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    internal void SetTransportForTest(IDisplayTransport transport)
    {
        lock (_lock)
        {
            _transport = transport;
        }
    }

    /// <summary>
    /// Starts the background touch polling loop and initiates connection attempts.
    /// </summary>
    public void Start()
    {
        if (Volatile.Read(ref _isDisposed) != 0) return;

        _touchPoll.Start();
        RunConnectAttempt(_connectLog, "Initial connection failed, will retry via reconnect timer");
        ArmReconnectTimer();
    }

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

        ArmReconnectTimer();
        RunConnectAttempt(_reconnectLog);
    }

    private void ArmReconnectTimer()
    {
        if (Volatile.Read(ref _isDisposed) != 0) return;
        TimeSpan delay = ReconnectBackoffPolicy.DelayFor(Volatile.Read(ref _consecutiveFailures));
        try
        {
            _reconnectTimer.Change(delay, delay);
        }
        catch (ObjectDisposedException)
        {
            // A dispose raced the arm: no timer left to re-arm.
        }
    }

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
        onTickFailure: () => FileLog.Write("Touch poll tick failed"),
        log: msg => FileLog.Write(msg));

    internal void TouchPollTick()
    {
        IDisplayTransport? transport;
        lock (_lock)
        {
            transport = _transport;
        }

        if (transport?.ReadTouch() is not TouchReport report)
            return;

        OnTouchEvent?.Invoke(new SKPoint(report.X, report.Y), TouchReport.ToEventType(report.Type));
    }

    internal bool TryConnect()
    {
        if (State == ConnectionState.Connected)
        {
            _tryConnectLog.Write("Already connected, skipping");
            return true;
        }

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
            DisconnectInternal();

            IDisplayTransport? transport = null;
            ConnectionState reached = ConnectionState.DeviceNotFound;
            bool disposedDuringConnect = false;

            try
            {
                transport = _transportFactory();
                reached = transport.Connect();
            }
            catch (Exception ex)
            {
                _connectLog.Write($"Connection exception: {ex.Message}");
                reached = ConnectionState.DeviceNotResponding;
            }

            if (reached == ConnectionState.Connected)
            {
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
                    reached = ConnectionState.Disconnected;
                }
                else
                {
                    State = ConnectionState.Connected;
                    _consecutiveFailures = 0;
                    ArmReconnectTimer();
                    FileLog.Write("Hardware connection successful!");
                }
            }
            else
            {
                DisposeTransport(transport);
            }

            if (reached != ConnectionState.Connected && !disposedDuringConnect)
            {
                lock (_lock)
                {
                    _transport = null;
                }
                State = reached;
                _consecutiveFailures++;
                FileLog.Write(ConnectVerdict.Line(reached, _consecutiveFailures));
            }

            return reached == ConnectionState.Connected;
        }
        finally
        {
            lock (_lock)
            {
                _connecting = false;
            }
        }
    }

    private static void DisposeTransport(IDisplayTransport? transport)
    {
        transport?.Dispose();
    }

    /// <summary>
    /// Sends an already encoded RGB565 frame to the display.
    /// </summary>
    /// <param name="rgb565">Raw RGB565 byte buffer.</param>
    /// <returns>Verdict of the send operation.</returns>
    public FrameSendResult SendFrameBytes(ReadOnlyMemory<byte> rgb565)
    {
        if (MemoryMarshal.TryGetArray(rgb565, out ArraySegment<byte> segment) && segment.Array is null)
        {
            return FrameSendResult.Refused;
        }

        IDisplayTransport? transport;
        lock (_lock)
        {
            transport = _transport;
        }

        if (Volatile.Read(ref _isDisposed) != 0 || State != ConnectionState.Connected || transport is null)
        {
            return FrameSendResult.Refused;
        }

        return transport.SendFrame(rgb565);
    }

    internal void SimulateTouch(float x, float y, TouchEventType eventType)
    {
        OnTouchEvent?.Invoke(new SKPoint(x, y), eventType);
    }

    private void DisconnectInternal()
    {
        IDisplayTransport? oldTransport;
        lock (_lock)
        {
            oldTransport = _transport;
            _transport = null;
            if (State == ConnectionState.Connected)
            {
                State = ConnectionState.Disconnected;
            }
        }

        if (oldTransport is not null)
        {
            BoundedWaitVerdict verdict = RunBounded(() => { oldTransport.Dispose(); return true; }, DisposeAbandonBudget);
            if (verdict.Error is not null)
            {
                _disposeLog.Write($"Transport disposal failed or timed out: {verdict.Error.Message}");
            }
        }
    }

    private sealed record BoundedWaitVerdict(bool Settled, bool Confirmed, Exception? Error);

    private static BoundedWaitVerdict RunBounded(Func<bool> work, TimeSpan budget)
    {
        try
        {
            var task = Task.Run(work);
            bool settled = task.Wait(budget);
            bool confirmed = false;
            if (settled)
            {
                confirmed = task.Result;
            }
            return new BoundedWaitVerdict(settled, confirmed, null);
        }
        catch (Exception ex)
        {
            Exception error = ex is AggregateException { InnerExceptions.Count: > 0 } aggregate
                ? aggregate.GetBaseException()
                : ex;
            return new BoundedWaitVerdict(false, false, error);
        }
    }

    /// <summary>
    /// Attempts to place the display into standby mode under a bounded timeout.
    /// </summary>
    /// <returns><c>true</c> if standby was confirmed; otherwise, <c>false</c>.</returns>
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
            return false;
        }

        BoundedWaitVerdict verdict = RunBounded(() => transport.GoToStandby(), StandbyCloseBudget);
        string? line = verdict.Error is null
            ? StandbyVerdict.NotConfirmedLine(verdict.Settled, verdict.Confirmed, hasTransport: true, duringDispose: false)
            : StandbyVerdict.FailureLine(verdict.Error, duringDispose: false);
        if (line is not null)
        {
            _standbyLog.Write(line);
        }
        return verdict.Confirmed;
    }

    /// <summary>
    /// Releases all managed and unmanaged resources held by this session.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0) return;

        _touchPoll.Dispose();
        _reconnectTimer.Dispose();

        IDisplayTransport? transport;
        lock (_lock)
        {
            transport = _transport;
        }

        BoundedWaitVerdict standbyVerdict = RunBounded(() => transport?.GoToStandby() ?? false, StandbyCloseBudget);
        string? line = standbyVerdict.Error is null
            ? StandbyVerdict.NotConfirmedLine(standbyVerdict.Settled, standbyVerdict.Confirmed, hasTransport: transport is not null, duringDispose: true)
            : StandbyVerdict.FailureLine(standbyVerdict.Error, duringDispose: true);
        if (line is not null)
        {
            _standbyLog.Write(line);
        }

        DisconnectInternal();
    }
}
