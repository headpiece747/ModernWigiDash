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
                    // Faulted/unreachable channels throw ServiceUnavailableException
                    // (never a null version), so a non-empty version here means the
                    // service is genuinely serving the contract.
                    string version = _wcfClient.GetVersion();

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
                // Drain all queued frames, keep only the latest; return the
                // dropped frames' pooled buffers to the pool.
                byte[]? latestFrame = ChannelFrameCoalescer.DrainToLatest(reader, _framePool.Release);

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
                finally
                {
                    _framePool.Release(latestFrame);
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
        TouchEventType type = touch.Type switch
        {
            DisplayProtocolConstants.TouchTypeDown => TouchEventType.TouchDown,
            DisplayProtocolConstants.TouchTypeUp => TouchEventType.TouchUp,
            _ => TouchEventType.TouchMove
        };

        var outcome = _gestureInterpreter.Feed(type, touch.X, touch.Y, _profile.Pages.Count, _profile.ActivePageIndex);
        ApplyGestureOutcome(outcome, touch.X, touch.Y);
    }

    /// <summary>
    /// Applies a gesture decision: performs page navigation, or routes the
    /// touch sample to the widget compositor. Shared by the USB-direct and
    /// WCF touch paths.
    /// </summary>
    private void ApplyGestureOutcome(Gestures.GestureOutcome outcome, float x, float y)
    {
        switch (outcome.PageAction)
        {
            case Gestures.GesturePageAction.NextPage:
                SwitchToPage(_profile.ActivePageIndex + 1);
                return;
            case Gestures.GesturePageAction.PrevPage:
                SwitchToPage(_profile.ActivePageIndex - 1);
                return;
        }

        if (outcome.RouteToWidgets)
        {
            SkiaFrameCompositor.RouteTouch(_profile.ActivePage, x, y, outcome.WidgetTouchType);
            SkiaCanvas.InvalidateVisual();
        }
    }

}
