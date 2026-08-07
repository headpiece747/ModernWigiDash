// <copyright file="DisplayDeviceEngine.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using System.Threading.Channels;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Hardware.Transport;

/// <summary>
/// Unified hardware engine for the USB display device.
/// Uses DisplayHidTransport for all USB communication - no vendor DLL dependencies.
/// All state is instance-owned: each engine owns its transport, frame queue, and
/// connection lifecycle. Callers that need one device per process create exactly
/// one engine (MainWindow does; the service does).
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

    // -- Frame Processing --
    private int _framesSent;
    private CancellationTokenSource? _frameQueueCts;
    private readonly Channel<SKBitmap> _frameChannel =
        Channel.CreateBounded<SKBitmap>(new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private DateTime _lastFrameSent = DateTime.MinValue;
    private static readonly TimeSpan MinFrameInterval =
        TimeSpan.FromMilliseconds(33); // ~30 FPS - device capability

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

        // Start this engine's frame processing loop.
        _frameQueueCts = new CancellationTokenSource();
        _ = Task.Run(() => ProcessFrameQueueAsync(_frameQueueCts.Token));

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
    /// Queues a frame buffer for delivery to the device display.
    /// Uses a bounded channel to prevent frame queue buildup.
    /// </summary>
    /// <returns>True when the frame was queued; false when the engine is not
    /// connected, disposed, or the bounded queue was full (frame dropped).</returns>
    public bool SendFrameBuffer(SKBitmap frameBitmap)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || !_connected || frameBitmap == null)
            return false;

        // Copy the frame to prevent disposal issues
        SKBitmap copy = frameBitmap.Copy();
        if (!_frameChannel.Writer.TryWrite(copy))
        {
            copy.Dispose();
            return false;
        }

        return true;
    }

    /// <summary>
    /// Simulates a touch event for testing.
    /// </summary>
    public void SimulateTouch(float x, float y, TouchEventType eventType)
    {
        OnTouchEvent?.Invoke(new SKPoint(x, y), eventType);
    }

    /// <summary>
    /// Background task that processes the frame queue and sends frames to the device.
    /// Uses proper rate limiting with async delays.
    /// </summary>
    private async Task ProcessFrameQueueAsync(CancellationToken ct)
    {
        int framesRead = 0;

        while (!ct.IsCancellationRequested)
        {
            SKBitmap? skFrame = null;
            try
            {
                skFrame = await _frameChannel.Reader.ReadAsync(ct);
                framesRead++;

                if (framesRead <= 3 || framesRead % 300 == 0)
                    Log($"[FrameQueue] Read frame #{framesRead}, connected={_connected}");

                // Rate limiting - delay if too soon since last frame (don't drop the frame)
                var elapsed = DateTime.Now - _lastFrameSent;
                if (elapsed < MinFrameInterval)
                {
                    await Task.Delay((int)(MinFrameInterval.TotalMilliseconds - elapsed.TotalMilliseconds));
                }

                // Snapshot connection state (minimize lock hold time)
                bool connected;
                DisplayHidTransport? transport;
                lock (_lock)
                {
                    connected = _connected;
                    transport = _transport;
                }

                if (!connected || transport == null)
                    continue;

                // Convert SKBitmap to RGB565 byte array (outside lock - expensive)
                byte[] frameBytes = FrameEncoder.ConvertToRgb565(skFrame!);

                // Send frame via transport (outside lock - may block on I/O)
#pragma warning disable S6966 // Transport SendFrame is synchronous by design for frame pacing
                bool success = transport.SendFrame(frameBytes);
#pragma warning restore S6966

                if (success)
                {
                    lock (_lock)
                    {
                        _framesSent++;
                        _lastFrameSent = DateTime.Now;
                    }
                    if (_framesSent <= 5 || _framesSent % 30 == 0)
                        Log($"Frame #{_framesSent} sent successfully");
                }
                else
                {
                    Log($"Frame send failed at frame #{_framesSent + 1}");
                }
            }
            catch (Exception ex)
            {
                Log($"[FrameQueue] Warning: {ex.Message}");
                await Task.Delay(100);
            }
            finally
            {
                skFrame?.Dispose();
            }
        }
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

        // Stop this engine's frame queue before the standby command so no frame
        // write races the welcome-screen switch.
        _frameQueueCts?.Cancel();
        _frameQueueCts?.Dispose();
        _frameQueueCts = null;

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
