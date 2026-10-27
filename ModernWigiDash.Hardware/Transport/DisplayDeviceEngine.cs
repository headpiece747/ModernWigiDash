// <copyright file="DisplayDeviceEngine.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using SkiaSharp;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Unified hardware engine for the USB display device.
/// Acts as the primary facade and composition root over <see cref="DisplayDeviceSession"/>.
/// All state is instance-owned: each engine delegates its transport and connection
/// lifecycle to a dedicated <see cref="DisplayDeviceSession"/>.
/// </summary>
public sealed class DisplayDeviceEngine : IDisposable
{
    private readonly DisplayDeviceSession _session;

    // Preserved for test reflection access in CloseBudgetPolicyTests
#pragma warning disable CS0649 // Field is assigned via reflection in CloseBudgetPolicyTests
#pragma warning disable IDE0044 // Add readonly modifier
    private IDisplayTransport? _transport;
#pragma warning restore IDE0044
#pragma warning restore CS0649

    internal TimeSpan ReconnectPeriod
    {
        get => _session.ReconnectPeriod;
        set => _session.ReconnectPeriod = value;
    }

    internal TimeSpan StandbyCloseBudget => _transport?.CloseBudgets.StandbyCloseBudget ?? _session.StandbyCloseBudget;
    internal TimeSpan DisposeAbandonBudget => _transport?.CloseBudgets.DisposeAbandonBudget ?? _session.DisposeAbandonBudget;

    internal void SetTransportForTest(IDisplayTransport transport)
    {
        _transport = transport;
        _session.SetTransportForTest(transport);
    }

    /// <summary>
    /// Gets the current connection state.
    /// </summary>
    public ConnectionState State => _session.State;

    /// <summary>
    /// Gets a value indicating whether frames can currently be dispatched.
    /// </summary>
    public bool CanSendFrames => _session.CanSendFrames;

    /// <summary>
    /// Raised when a normalized touch report is received.
    /// </summary>
    public event Action<SKPoint, TouchEventType>? OnTouchEvent
    {
        add => _session.OnTouchEvent += value;
        remove => _session.OnTouchEvent -= value;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="DisplayDeviceEngine"/> class.
    /// </summary>
    public DisplayDeviceEngine() : this((Func<IDisplayTransport>?)null)
    {
    }

    internal DisplayDeviceEngine(Func<IDisplayTransport>? transportFactory)
    {
        _session = new DisplayDeviceSession(transportFactory);
    }

    internal DisplayDeviceEngine(IDisplayTransport transport, ConnectionState initialState = ConnectionState.Disconnected)
    {
        _transport = transport;
        _session = new DisplayDeviceSession(transport, initialState);
    }

    /// <summary>
    /// Starts the engine's background touch polling loop and initiates connection attempts.
    /// </summary>
    public void Start() => _session.Start();

    internal void ReconnectTick(object? state) => _session.ReconnectTick(state);

    internal void TouchPollTick() => _session.TouchPollTick();

    internal bool TryConnect() => _session.TryConnect();

    /// <summary>
    /// Sends an already encoded RGB565 frame to the display.
    /// </summary>
    /// <param name="rgb565">Raw RGB565 byte buffer.</param>
    /// <returns>Verdict of the send operation.</returns>
    public FrameSendResult SendFrameBytes(ReadOnlyMemory<byte> rgb565) => _session.SendFrameBytes(rgb565);

    internal void SimulateTouch(float x, float y, TouchEventType eventType) => _session.SimulateTouch(x, y, eventType);

    /// <summary>
    /// Attempts to place the display into standby mode under a bounded timeout.
    /// </summary>
    /// <returns><c>true</c> if standby was confirmed; otherwise, <c>false</c>.</returns>
    public bool TryGoToStandby() => _session.TryGoToStandby();

    /// <summary>
    /// Releases all managed and unmanaged resources held by this engine.
    /// </summary>
    public void Dispose() => _session.Dispose();
}
