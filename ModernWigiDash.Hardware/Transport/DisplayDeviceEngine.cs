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
/// one engine (MainWindow does; the service does).
///
/// Frame delivery (encode → pool → coalesce → paced send) does NOT live here:
/// the App binds a <see cref="FrameDelivery"/> instance to
/// <see cref="SendFrameBytes"/> and the engine only owns connection, standby,
/// and touch. One delivery policy, every transport.
/// </summary>
public sealed class DisplayDeviceEngine : IDisposable
{
    // -- Constants --
    public const int ScreenWidth = DisplayProtocolConstants.FramebufferWidth;  // 1016
    public const int ScreenHeight = DisplayProtocolConstants.FramebufferHeight; // 592
    public const int FrameBufferSize = DisplayProtocolConstants.FrameBufferSize; // 1,202,944 bytes (1016 * 592 * 2)

    // -- Connection State --
    private DisplayHidTransport? _transport;
    private bool _connected;
    private bool _connecting; // Prevent concurrent connection attempts
    private bool _serviceActive; // Yielded to the ModernWigiDash service
    private readonly Lock _lock = new();

    // -- Lifecycle State --
    private int _isDisposed;
    private readonly Timer _reconnectTimer;

    // -- Public Properties --
    public bool IsConnected => _connected;
    public bool IsHardwareActive { get; private set; }
    public bool IsSimulationMode { get; private set; } = true;
    public string DeviceStatus { get; private set; } = "🟡 Initializing...";

    // -- Events --
    public event Action<SKPoint, TouchEventType>? OnTouchEvent;

    /// <summary>
    /// Initializes a new instance of the device engine.
    /// </summary>
    public DisplayDeviceEngine()
    {
        Log("=== Display Hardware Engine Initializing ===");

        // Attempt initial connection (fire-and-forget with proper exception handling)
        _ = TryConnectAsync().ContinueWith(t =>
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

        // Setup reconnection timer
        _reconnectTimer = new Timer(_ =>
        {
            bool shouldReconnect;
            lock (_lock)
            {
                shouldReconnect = Volatile.Read(ref _isDisposed) == 0 && !_connected && !_connecting;
            }
            if (shouldReconnect)
            {
                _ = TryConnectAsync().ConfigureAwait(false);
            }
        }, null, 5000, 5000);
    }

    /// <summary>
    /// Attempts to connect to the physical device asynchronously.
    /// Guards against concurrent connection attempts to prevent connection churn.
    /// </summary>
    public async Task<bool> TryConnectAsync()
    {
        // Fast-path: already connected
        if (_connected)
        {
            Log("[TryConnectAsync] Already connected, skipping");
            return true;
        }

        // Yield hardware management if the ModernWigiDash service is active.
        // Check both the Windows Service and any running service process.
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("ModernWigiDashService");
            if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                if (!_serviceActive)
                {
                    Log("[TryConnectAsync] ModernWigiDashService Windows Service is running. Yielding USB hardware handle to service.");
                    _serviceActive = true;
                }
                IsSimulationMode = false;
                IsHardwareActive = true;
                DeviceStatus = "🟢 Worker Service Active";
                return true;
            }
        }
        catch
        {
            // Service check failed (permissions, etc.) — fall through
            System.Diagnostics.Debug.WriteLine("Service check failed; falling through to direct connection");
        }

        // Also check for a running service process (e.g. "-test" mode)
        try
        {
            var svcProcesses = System.Diagnostics.Process.GetProcessesByName("ModernWigiDash.Service");
            if (svcProcesses.Length > 0)
            {
                if (!_serviceActive)
                {
                    Log($"[TryConnectAsync] ModernWigiDash.Service process running (PID={svcProcesses[0].Id}). Yielding USB hardware handle to service.");
                    _serviceActive = true;
                }
                foreach (var p in svcProcesses) p.Dispose();
                IsSimulationMode = false;
                IsHardwareActive = true;
                DeviceStatus = "🟢 Worker Service Active";
                return true;
            }
        }
        catch
        {
            // Process check failed — fall through to direct connection
            System.Diagnostics.Debug.WriteLine("Process check failed; falling through to direct connection");
        }

        // Guard against concurrent connection attempts
        lock (_lock)
        {
            if (_connecting || Volatile.Read(ref _isDisposed) != 0)
            {
                Log($"[TryConnectAsync] Connection in progress or disposed, skipping (connected={_connected})");
                return _connected;
            }
            _connecting = true;
        }

        try
        {
            // Disconnect any existing transport before attempting new connection
            DisconnectInternal();

            DisplayHidTransport? transport = null;
            bool connected = false;

            try
            {
                transport = new DisplayHidTransport();
                connected = transport.Connect();

                if (connected)
                {
                    lock (_lock)
                    {
                        _transport = transport;
                        _connected = true;
                    }
                    _serviceActive = false;
                    IsSimulationMode = false;

                    // Send device initialization sequence (PING + blank frame + GoToScreen)
                    // This puts the device in the correct state to receive frames
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(100); // Small delay after connection
                            transport.SendInitCommands();
                            Log("[INIT] Device initialization sequence completed");
                        }
                        catch (Exception ex)
                        {
                            Log($"[INIT] Device initialization failed: {ex.Message}");
                        }
                    });

                    DeviceStatus = "🟢 Physical Device Active";
                    IsHardwareActive = true;
                    Log("Hardware connection successful!");
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

            if (!connected)
            {
                lock (_lock)
                {
                    _transport = null;
                    _connected = false;
                }
                _serviceActive = false;
                IsSimulationMode = true;
                IsHardwareActive = false;
                DeviceStatus = "🟡 Device Unavailable (Simulation Mode)";
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
        if (Volatile.Read(ref _isDisposed) != 0 || !_connected || rgb565 == null || rgb565.Length == 0)
            return false;

        DisplayHidTransport? transport;
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
    /// Simulates a touch event for testing.
    /// </summary>
    public void SimulateTouch(float x, float y, TouchEventType eventType)
    {
        OnTouchEvent?.Invoke(new SKPoint(x, y), eventType);
    }

    /// <summary>
    /// Disconnects from the device and cleans up resources.
    /// </summary>
    private void DisconnectInternal()
    {
        DisplayHidTransport? oldTransport;
        lock (_lock)
        {
            _connected = false;
            IsHardwareActive = false;
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

        _reconnectTimer.Dispose();

        // Direct-USB mode owns the device, so the app is responsible for putting
        // the display into standby when it exits. In WCF mode (_transport is
        // null because the service owns the device) the service handles standby
        // via the Shutdown operation.
        try
        {
            _transport?.GoToStandby();
        }
        catch (Exception ex)
        {
            Log($"[STANDBY] Standby failed during dispose: {ex.Message}");
        }

        DisconnectInternal();
    }
}
