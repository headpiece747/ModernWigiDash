// <copyright file="DisplayDeviceEngine.cs" company="ModernWigiDash">
// Copyright (c) ModernWigiDash. All rights reserved.
// Licensed under the MIT license.
// </copyright>

using System.Threading.Channels;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Hardware;

/// <summary>
/// Unified hardware engine for the USB display device.
/// Uses DisplayHidTransport for all USB communication - no vendor DLL dependencies.
/// .NET 10: Uses proper async/await, Channels, and structured concurrency.
/// </summary>
public sealed class DisplayDeviceEngine : IDisposable
{
    // -- Constants --
    public const int ScreenWidth = DisplayProtocolConstants.FramebufferWidth;  // 1024
    public const int ScreenHeight = DisplayProtocolConstants.FramebufferHeight; // 600
    public const int FrameBufferSize = DisplayProtocolConstants.FrameBufferSize; // 1,228,800 bytes (1024 * 600 * 2)

    private static readonly string LogPath =
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "display_device.log");

    // -- Static Connection Guard --
    private static DisplayHidTransport? _sTransport;
    private static bool _sConnected;
    private static bool _sConnecting; // Prevent concurrent connection attempts
    private static bool _sServiceActive; // Yielded to the ModernWigiDash service
    private static readonly Lock _sLock = new();

    // -- Static Frame Processing --
    private static bool _sFrameQueueStarted;
    private static int _sFramesSent;
    private static CancellationTokenSource? _sFrameQueueCts;
    private static readonly Channel<SKBitmap> _sFrameChannel =
        Channel.CreateBounded<SKBitmap>(new BoundedChannelOptions(5)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private static DateTime _sLastFrameSent = DateTime.MinValue;
    private static readonly TimeSpan MinFrameInterval =
        TimeSpan.FromMilliseconds(33); // ~30 FPS - device capability

    // -- Instance State --
    private int _isDisposed;
    private readonly Timer _reconnectTimer;

    // -- Public Properties --
    public bool IsConnected => _sConnected;
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

        // Start frame processing (only once across all instances)
        if (!_sFrameQueueStarted)
        {
            _sFrameQueueStarted = true;
            _sFrameQueueCts = new CancellationTokenSource();
            _ = Task.Run(() => ProcessFrameQueueAsync(_sFrameQueueCts.Token));
        }

        // Setup reconnection timer
        _reconnectTimer = new Timer(_ =>
        {
            bool shouldReconnect;
            lock (_sLock)
            {
                shouldReconnect = Volatile.Read(ref _isDisposed) == 0 && !_sConnected && !_sConnecting;
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
        if (_sConnected)
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
                if (!_sServiceActive)
                {
                    Log("[TryConnectAsync] ModernWigiDashService Windows Service is running. Yielding USB hardware handle to service.");
                    _sServiceActive = true;
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
                if (!_sServiceActive)
                {
                    Log($"[TryConnectAsync] ModernWigiDash.Service process running (PID={svcProcesses[0].Id}). Yielding USB hardware handle to service.");
                    _sServiceActive = true;
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

        // Also check if the WCF endpoint is responding (covers -test mode where process is "dotnet")
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync("localhost", 8733).ConfigureAwait(false);
            if (tcp.Connected)
            {
                if (!_sServiceActive)
                {
                    Log("[TryConnectAsync] WCF service detected on port 8733. Yielding USB hardware handle to service.");
                    _sServiceActive = true;
                }
                IsSimulationMode = false;
                IsHardwareActive = true;
                DeviceStatus = "🟢 Worker Service Active";
                return true;
            }
        }
        catch
        {
            // WCF check failed — fall through to direct connection
            System.Diagnostics.Debug.WriteLine("WCF check failed; falling through to direct connection");
        }

        // Guard against concurrent connection attempts
        lock (_sLock)
        {
            if (_sConnecting || Volatile.Read(ref _isDisposed) != 0)
            {
                Log($"[TryConnectAsync] Connection in progress or disposed, skipping (connected={_sConnected})");
                return _sConnected;
            }
            _sConnecting = true;
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
                connected = await transport.ConnectAsync();

                if (connected)
                {
                    lock (_sLock)
                    {
                        _sTransport = transport;
                        _sConnected = true;
                    }
                    _sServiceActive = false;
                    IsSimulationMode = false;

                    // Send device initialization sequence (PING + blank frame + GoToScreen)
                    // This puts the device in the correct state to receive frames
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(100); // Small delay after connection
                            await transport.SendInitCommandsAsync();
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
                lock (_sLock)
                {
                    _sTransport = null;
                    _sConnected = false;
                }
                _sServiceActive = false;
                IsSimulationMode = true;
                IsHardwareActive = false;
                DeviceStatus = "🟡 Device Unavailable (Simulation Mode)";
                Log("No physical device found - running in simulation mode");
            }

            return connected;
        }
        finally
        {
            lock (_sLock)
            {
                _sConnecting = false;
            }
        }
    }

    /// <summary>
    /// Synchronous connection wrapper for legacy callers.
    /// </summary>
    public bool TryConnect()
    {
#pragma warning disable S6966 // Intentional sync wrapper — callers require synchronous connection check
        return TryConnectAsync().GetAwaiter().GetResult();
#pragma warning restore S6966
    }

    /// <summary>
    /// Sends a frame buffer to the device display.
    /// Uses a bounded channel to prevent frame queue buildup.
    /// </summary>
    public void SendFrameBuffer(SKBitmap frameBitmap)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || !_sConnected || frameBitmap == null)
            return;

        // Copy the frame to prevent disposal issues
        SKBitmap copy = frameBitmap.Copy();
        if (!_sFrameChannel.Writer.TryWrite(copy))
        {
            copy.Dispose();
        }
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
                skFrame = await _sFrameChannel.Reader.ReadAsync(ct);
                framesRead++;

                if (framesRead <= 3 || framesRead % 300 == 0)
                    Log($"[FrameQueue] Read frame #{framesRead}, connected={_sConnected}");

                // Rate limiting - delay if too soon since last frame (don't drop the frame)
                var elapsed = DateTime.Now - _sLastFrameSent;
                if (elapsed < MinFrameInterval)
                {
                    await Task.Delay((int)(MinFrameInterval.TotalMilliseconds - elapsed.TotalMilliseconds));
                }

                // Snapshot connection state (minimize lock hold time)
                bool connected;
                DisplayHidTransport? transport;
                lock (_sLock)
                {
                    connected = _sConnected;
                    transport = _sTransport;
                }

                if (!connected || transport == null)
                    continue;

                // Convert SKBitmap to RGB565 byte array (outside lock - expensive)
                byte[] frameBytes = ConvertToRgb565(skFrame!);

                // Send frame via transport (outside lock - may block on I/O)
#pragma warning disable S6966 // Transport SendFrame is synchronous by design for frame pacing
                bool success = transport.SendFrame(frameBytes);
#pragma warning restore S6966

                if (success)
                {
                    lock (_sLock)
                    {
                        _sFramesSent++;
                        _sLastFrameSent = DateTime.Now;
                    }
                    if (_sFramesSent <= 5 || _sFramesSent % 30 == 0)
                        Log($"Frame #{_sFramesSent} sent successfully");
                }
                else
                {
                    Log($"Frame send failed at frame #{_sFramesSent + 1}");
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
    /// Converts an SKBitmap to RGB565 little-endian byte array.
    /// </summary>
    private static byte[] ConvertToRgb565(SKBitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        byte[] rgb565 = new byte[width * height * 2];

        using var pixmap = bitmap.PeekPixels();
        unsafe
        {
            byte* srcPtr = (byte*)pixmap.GetPixels();
            fixed (byte* dstPtr = rgb565)
            {
                ushort* dstUshort = (ushort*)dstPtr;
                int pixelCount = width * height;

                for (int i = 0; i < pixelCount; i++)
                {
                    byte b = srcPtr[i * 4];
                    byte g = srcPtr[i * 4 + 1];
                    byte r = srcPtr[i * 4 + 2];

                    // Convert RGBA to RGB565
                    ushort val = (ushort)(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
                    dstUshort[i] = val;
                }
            }
        }

        return rgb565;
    }

    /// <summary>
    /// Disconnects from the device and cleans up resources.
    /// </summary>
    private void DisconnectInternal()
    {
        DisplayHidTransport? oldTransport;
        lock (_sLock)
        {
            _sConnected = false;
            IsHardwareActive = false;
            oldTransport = _sTransport;
            _sTransport = null;
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
    private static void Log(string msg)
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var sw = new StreamWriter(fs);
            sw.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }
        catch (IOException)
        {
            // Log file may be locked or unavailable; silently ignore
            System.Diagnostics.Debug.WriteLine("Log file write failed (may be locked or unavailable); ignoring");
        }
    }

    /// <summary>
    /// Releases all resources used by the engine.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0) return;

        _reconnectTimer.Dispose();
        _sFrameQueueCts?.Cancel();
        _sFrameQueueCts?.Dispose();
        // Reset the static queue state so a later engine instance can start a
        // fresh frame queue instead of reusing (and crashing on) the disposed CTS.
        _sFrameQueueCts = null;
        _sFrameQueueStarted = false;
        DisconnectInternal();
    }
}
