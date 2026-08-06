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
/// .NET 10: Uses proper async/await, Channels, and structured concurrency.
/// </summary>
public sealed class DisplayDeviceEngine : IDisposable
{
    // -- Constants --
    public const int ScreenWidth = DisplayProtocolConstants.FramebufferWidth;  // 1016
    public const int ScreenHeight = DisplayProtocolConstants.FramebufferHeight; // 592
    public const int FrameBufferSize = DisplayProtocolConstants.FrameBufferSize; // 1,202,944 bytes (1016 * 592 * 2)

    // -- Static Connection Guard --
    private static DisplayHidTransport? sTransport;
    private static bool sConnected;
    private static bool sConnecting; // Prevent concurrent connection attempts
    private static bool sServiceActive; // Yielded to the ModernWigiDash service
    private static readonly Lock sLock = new();

    // -- Static Frame Processing --
    private static bool sFrameQueueStarted;
    private static int sFramesSent;
    private static CancellationTokenSource? sFrameQueueCts;
    private static int sInstanceCount;
    private static readonly Channel<SKBitmap> sFrameChannel =
        Channel.CreateBounded<SKBitmap>(new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private static DateTime sLastFrameSent = DateTime.MinValue;
    private static readonly TimeSpan MinFrameInterval =
        TimeSpan.FromMilliseconds(33); // ~30 FPS - device capability

    // -- Instance State --
    private int _isDisposed;
    private readonly Timer _reconnectTimer;

    // -- Public Properties --
    public bool IsConnected => sConnected;
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

        // Start frame processing (only once across all instances). The queue is
        // reference-counted: it stops only when the LAST live instance disposes.
        Interlocked.Increment(ref sInstanceCount);
        if (!sFrameQueueStarted)
        {
            sFrameQueueStarted = true;
            sFrameQueueCts = new CancellationTokenSource();
            _ = Task.Run(() => ProcessFrameQueueAsync(sFrameQueueCts.Token));
        }

        // Setup reconnection timer
        _reconnectTimer = new Timer(_ =>
        {
            bool shouldReconnect;
            lock (sLock)
            {
                shouldReconnect = Volatile.Read(ref _isDisposed) == 0 && !sConnected && !sConnecting;
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
        if (sConnected)
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
                if (!sServiceActive)
                {
                    Log("[TryConnectAsync] ModernWigiDashService Windows Service is running. Yielding USB hardware handle to service.");
                    sServiceActive = true;
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
                if (!sServiceActive)
                {
                    Log($"[TryConnectAsync] ModernWigiDash.Service process running (PID={svcProcesses[0].Id}). Yielding USB hardware handle to service.");
                    sServiceActive = true;
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
        lock (sLock)
        {
            if (sConnecting || Volatile.Read(ref _isDisposed) != 0)
            {
                Log($"[TryConnectAsync] Connection in progress or disposed, skipping (connected={sConnected})");
                return sConnected;
            }
            sConnecting = true;
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
                    lock (sLock)
                    {
                        sTransport = transport;
                        sConnected = true;
                    }
                    sServiceActive = false;
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
                lock (sLock)
                {
                    sTransport = null;
                    sConnected = false;
                }
                sServiceActive = false;
                IsSimulationMode = true;
                IsHardwareActive = false;
                DeviceStatus = "🟡 Device Unavailable (Simulation Mode)";
                Log("No physical device found - running in simulation mode");
            }

            return connected;
        }
        finally
        {
            lock (sLock)
            {
                sConnecting = false;
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
        if (Volatile.Read(ref _isDisposed) != 0 || !sConnected || frameBitmap == null)
            return false;

        // Copy the frame to prevent disposal issues
        SKBitmap copy = frameBitmap.Copy();
        if (!sFrameChannel.Writer.TryWrite(copy))
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
    /// Fixed: removed _sBusyStreaming flag that caused thread starvation.
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
                skFrame = await sFrameChannel.Reader.ReadAsync(ct);
                framesRead++;

                if (framesRead <= 3 || framesRead % 300 == 0)
                    Log($"[FrameQueue] Read frame #{framesRead}, connected={sConnected}");

                // Rate limiting - delay if too soon since last frame (don't drop the frame)
                var elapsed = DateTime.Now - sLastFrameSent;
                if (elapsed < MinFrameInterval)
                {
                    await Task.Delay((int)(MinFrameInterval.TotalMilliseconds - elapsed.TotalMilliseconds));
                }

                // Snapshot connection state (minimize lock hold time)
                bool connected;
                DisplayHidTransport? transport;
                lock (sLock)
                {
                    connected = sConnected;
                    transport = sTransport;
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
                    lock (sLock)
                    {
                        sFramesSent++;
                        sLastFrameSent = DateTime.Now;
                    }
                    if (sFramesSent <= 5 || sFramesSent % 30 == 0)
                        Log($"Frame #{sFramesSent} sent successfully");
                }
                else
                {
                    Log($"Frame send failed at frame #{sFramesSent + 1}");
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
        lock (sLock)
        {
            sConnected = false;
            IsHardwareActive = false;
            oldTransport = sTransport;
            sTransport = null;
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

        // Stop the shared frame queue only when the last live instance disposes.
        if (Interlocked.Decrement(ref sInstanceCount) <= 0)
        {
            sFrameQueueCts?.Cancel();
            sFrameQueueCts?.Dispose();
            sFrameQueueCts = null;
            sFrameQueueStarted = false;
        }
        DisconnectInternal();
    }
}
