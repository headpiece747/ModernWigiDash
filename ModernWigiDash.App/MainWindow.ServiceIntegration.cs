using System;
using System.Threading;
using System.Threading.Channels;
using System.Windows;
using ModernWigiDash.Hardware;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;
using ModernWigiDash.Service.Contracts;
using ModernWigiDash.Service.Wcf;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.App;

/// <summary>
/// MainWindow partial: service routing, frame sender, and telemetry poll loops.
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// Detects if the ModernWigiDash.Windows Service is running and initializes WCF client.
    /// When the service is active, it owns the USB device handle (running as LocalSystem).
    /// The App routes frames through WCF instead of connecting directly.
    /// </summary>
    /// <summary>
    /// Throttled retry of service routing when the engine yielded to a running
    /// service but the initial WCF detection failed.
    /// </summary>
    private void TryRetryServiceRouting()
    {
        if ((DateTime.Now - _lastWcfRetry).TotalSeconds < 10) return;
        _lastWcfRetry = DateTime.Now;
        _ = InitializeWcfRoutingAsync();
    }

    private async Task InitializeWcfRoutingAsync()
    {
        try
        {
            Log("[WCF] Detecting named pipe service...");
            string? pipeEndpoint = await ModernWigiDashDisplayServiceClient.DetectServicePortAsync();
            if (pipeEndpoint != null)
            {
                Log($"[WCF] Pipe {pipeEndpoint} detected, creating client...");
                _wcfClient = new ModernWigiDashDisplayServiceClient(pipeEndpoint);

                try
                {
                    string version = _wcfClient.GetVersion();
                    if (string.IsNullOrEmpty(version))
                    {
                        // Fault recovery returns null for a dead/unreachable
                        // channel; a null version means the service is NOT
                        // actually serving, so treat it as disconnected.
                        Log("[WCF] Connected but GetVersion returned null (service unreachable)");
                        _wcfClient?.Dispose();
                        _wcfClient = null;
                        _serviceActive = false;
                        return;
                    }

                    _serviceActive = true;
                    Log($"[WCF] Connected! Version: {version}, Endpoint: {pipeEndpoint}");

                    bool displayInit = _wcfClient.InitializeDisplay();
                    Log($"[WCF] Display initialization: {displayInit}");

                    StartTouchPolling();
                    StartSensorPolling();
                    StartFrameTimePolling();
                }
                catch (Exception ex)
                {
                    Log($"[WCF] Connected but GetVersion failed: {ex.Message}");
                    _wcfClient?.Dispose();
                    _wcfClient = null;
                    _serviceActive = false;
                }
            }
            else
            {
                Log("[WCF] No service detected. Using direct USB mode.");
            }
        }
        catch (Exception ex)
        {
            Log($"[WCF] Detection failed ({ex.Message}). Using direct USB mode.");
        }
    }

    /// <summary>
    /// Background loop that drains the frame channel and sends via WCF.
    /// When multiple frames queue up (because WCF round-trip is slower than
    /// the render timer), it drains all available frames and only sends the
    /// latest one. This keeps the display showing real-time content instead
    /// of replaying stale buffered frames.
    /// </summary>
    private async Task FrameSenderLoop(CancellationToken ct)
    {
        var reader = _frameChannel.Reader;
        int sentCount = 0;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                // Drain all queued frames, keep only the latest
                byte[]? latestFrame = ChannelFrameCoalescer.DrainToLatest(reader);

                if (latestFrame == null) continue;

                try
                {
                    bool ok = _wcfClient?.SendFrame(latestFrame) == true;
                    sentCount++;
                    if (sentCount <= 5 || sentCount % 120 == 0)
                        Log($"[WCF] Frame #{sentCount} sent ({latestFrame.Length} bytes) ok={ok}");
                }
                catch (Exception ex)
                {
                    Log($"[WCF] Frame send failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: frame sender loop cancelled during shutdown
            System.Diagnostics.Debug.WriteLine("Frame sender loop cancelled during shutdown");
        }
    }

    /// <summary>
    /// Starts polling for hardware touch input via WCF at 50ms intervals.
    /// Runs on a background thread to avoid blocking the WPF UI.
    /// Routes touch events to SkiaFrameCompositor and handles page swipe navigation.
    /// </summary>
    private void StartTouchPolling()
    {
        if (_touchPollCts != null) return;

        _touchPollCts = new CancellationTokenSource();
        var ct = _touchPollCts.Token;

        _ = Task.Run(async () =>
        {
            Log("[TOUCH] Touch polling started (50ms interval via WCF, background thread)");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_wcfClient == null || !_serviceActive)
                    {
                        await Task.Delay(500, ct);
                        continue;
                    }

                    var touch = _wcfClient.PollTouch();
                    if (touch != null)
                    {
                        await Dispatcher.BeginInvoke(() =>
                        {
                            ProcessHardwareTouch(touch);
                        });
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Log($"[WCF] Touch poll failed: {ex.Message}");
                }

                try { await Task.Delay(16, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    /// <summary>
    /// Background loop that polls the service for the latest hardware sensor
    /// snapshot (~1s) and caches it in <see cref="LhmSensorStore"/> so widgets
    /// read it on the render thread without a WCF round-trip.
    /// </summary>
    private void StartSensorPolling()
    {
        if (_sensorPollCts != null) return;

        _sensorPollCts = new CancellationTokenSource();
        var ct = _sensorPollCts.Token;

        _ = Task.Run(async () =>
        {
            Log("[SENSOR] Sensor polling started (1s interval via WCF, background thread)");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_wcfClient == null || !_serviceActive)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    var dto = _wcfClient.GetSensorSnapshot();
                    LhmSensorStore.UpdateFromDto(dto);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Log($"[WCF] Sensor poll failed: {ex.Message}");
                }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    /// <summary>
    /// Resolves the process id of the currently focused (foreground) window.
    /// Returns 0 when there is no foreground window.
    /// </summary>
    private static int GetForegroundProcessId()
    {
        try
        {
            IntPtr hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return 0;
            }

            GetWindowThreadProcessId(hwnd, out uint pid);
            return unchecked((int)pid);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Background loop that polls the service for the latest FPS / frame-time
    /// snapshot (~1s) and caches it in <see cref="FrameTimeStore"/> so the
    /// frame-time widget reads it on the render thread without a WCF round-trip.
    /// </summary>
    private void StartFrameTimePolling()
    {
        if (_frameTimePollCts != null) return;

        _frameTimePollCts = new CancellationTokenSource();
        var ct = _frameTimePollCts.Token;

        _ = Task.Run(async () =>
        {
            Log("[FRAME] Frame-time polling started (1s interval via WCF, background thread)");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_wcfClient == null || !_serviceActive)
                    {
                        await Task.Delay(1000, ct);
                        continue;
                    }

                    // Track the foreground window's process so the widget shows
                    // the focused game's FPS. When the App itself (or nothing)
                    // is focused, pass -1 so the service returns the idle view
                    // instead of falling back to the most active presenter.
                    int preferredPid = GetForegroundProcessId();
                    if (preferredPid <= 0 || preferredPid == Environment.ProcessId)
                    {
                        preferredPid = -1;
                    }

                    var dto = _wcfClient.GetFrameTimeSnapshot(preferredPid);
                    FrameTimeStore.UpdateFromDto(dto);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Log($"[WCF] Frame-time poll failed: {ex.Message}");
                }

                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }, ct);
    }
    /// Handles page swipe navigation and routes to widget compositor.
    /// Hardware touch protocol: None=0, Down=1 (contact+movement), Up=2 (release).
    /// Intermediate movement points during a swipe are also sent as Down(1).
    /// </summary>
    private void ProcessHardwareTouch(TouchEventInfo touch)
    {
        if (touch.Type == DisplayProtocolConstants.TouchTypeDown)
        {
            // Only record start position on the first Down (not intermediate movement points)
            if (!_hwTouchActive)
            {
                _hwTouchStartX = touch.X;
                _hwTouchStartY = touch.Y;
                _hwTouchActive = true;
            }
        }
        else if (touch.Type == DisplayProtocolConstants.TouchTypeUp && _hwTouchActive)
        {
            _hwTouchActive = false;
            float deltaX = touch.X - _hwTouchStartX;
            float deltaY = touch.Y - _hwTouchStartY;

            if (_profile.Pages.Count > 1)
            {
                // Swipe detection
                if (Math.Abs(deltaX) > 70 && Math.Abs(deltaY) < 80)
                {
                    if (deltaX < -70 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
                    {
                        SwitchToPage(_profile.ActivePageIndex + 1);
                        return;
                    }
                    else if (deltaX > 70 && _profile.ActivePageIndex > 0)
                    {
                        SwitchToPage(_profile.ActivePageIndex - 1);
                        return;
                    }
                }

                // Arrow tap fallback (stationary tap near edges)
                if (Math.Abs(deltaX) < 30 && Math.Abs(deltaY) < 30)
                {
                    if (touch.X <= 60 && touch.Y >= 200 && touch.Y <= 400 && _profile.ActivePageIndex > 0)
                    {
                        SwitchToPage(_profile.ActivePageIndex - 1);
                        return;
                    }
                    if (touch.X >= 964 && touch.Y >= 200 && touch.Y <= 400 && _profile.ActivePageIndex < _profile.Pages.Count - 1)
                    {
                        SwitchToPage(_profile.ActivePageIndex + 1);
                        return;
                    }
                }
            }
        }
        else if (touch.Type == DisplayProtocolConstants.TouchTypeUp)
        {
            // The device reports the release state for more than one poll.
            // Ignore subsequent releases so one physical tap becomes one action.
            return;
        }

        // Map hardware touch type to widget touch event
        // Hardware only sends: Down(1) for contact+movement, Up(2) for release
        TouchEventType touchEventType;
        if (touch.Type == DisplayProtocolConstants.TouchTypeDown && _hwTouchActive &&
            (Math.Abs(_hwTouchStartX - touch.X) > 0.5f || Math.Abs(_hwTouchStartY - touch.Y) > 0.5f))
        {
            touchEventType = TouchEventType.TouchMove;
        }
        else
        {
            touchEventType = touch.Type switch
            {
                DisplayProtocolConstants.TouchTypeDown => TouchEventType.TouchDown,
                DisplayProtocolConstants.TouchTypeUp => TouchEventType.TouchUp,
                _ => TouchEventType.TouchMove
            };
        }

        SkiaFrameCompositor.RouteTouch(_profile.ActivePage, touch.X, touch.Y, touchEventType);
        SkiaCanvas.InvalidateVisual();
    }

}
