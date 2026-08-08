using System;
using System.Threading;
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
    /// service but the initial WCF detection failed. Throttling lives in
    /// <see cref="ServiceRouting.ServiceRoutingState"/> — this is the re-detect
    /// trigger itself.
    /// </summary>
    private void TryRetryServiceRouting()
    {
        _ = InitializeWcfRoutingAsync();
    }

    /// <summary>True when the WCF client is bound and the routing state is active.</summary>
    private bool ServiceReady() => _wcfClient != null && _routingState.IsServiceActive;

    private async Task InitializeWcfRoutingAsync()
    {
        try
        {
            Log("[WCF] Detecting named pipe service...");
            string? pipeEndpoint = await ModernWigiDashDisplayServiceClient.DetectServicePortAsync();
            if (pipeEndpoint != null)
            {
                Log($"[WCF] Pipe {pipeEndpoint} detected, creating client...");

                // Dispose any previous client first: a re-detect while the old
                // channel is alive would strand its ChannelFactory, the 3MB
                // BufferManager pool, and the pipe handle per reconnect cycle.
                _wcfClient?.Dispose();
                _wcfClient = new ModernWigiDashDisplayServiceClient(pipeEndpoint);

                try
                {
                    // Faulted/unreachable channels throw ServiceUnavailableException
                    // (never a null version), so a non-empty version here means the
                    // service is genuinely serving the contract.
                    string version = _wcfClient.GetVersion();

                    _routingState.MarkActive();
                    Log($"[WCF] Connected! Version: {version}, Endpoint: {pipeEndpoint}");

                    bool displayInit = _wcfClient.InitializeDisplay();
                    Log($"[WCF] Display initialization: {displayInit}");

                    _wcfSink.AttachSend(_wcfClient.SendFrame);

                    // Assert exclusive touch-channel ownership so a rogue local
                    // process cannot drain touch events ahead of the App.
                    bool touchOwned = _wcfClient.AcquireTouchConsumer();
                    Log($"[WCF] Touch consumer acquired: {touchOwned}");

                    _touchPoll.Start();
                    _frameTimePoll.Start();
                }
                catch (Exception ex)
                {
                    Log($"[WCF] Connected but GetVersion failed: {ex.Message}");
                    _wcfClient?.Dispose();
                    _wcfClient = null;
                    _routingState.MarkInactive();
                    _wcfSink.AttachSend(null);
                }
            }
            else
            {
                Log("[WCF] No service detected. Using direct USB mode.");
                _wcfSink.AttachSend(null);
            }
        }
        catch (Exception ex)
        {
            Log($"[WCF] Detection failed ({ex.Message}). Using direct USB mode.");
            _wcfSink.AttachSend(null);
        }
    }

    /// <summary>
    /// One touch probe: polls the service for hardware touch and marshals a
    /// non-null sample to the UI thread for widget routing.
    /// </summary>
    private void TouchPollTick()
    {
        var touch = _wcfClient?.PollTouch();
        if (touch != null)
        {
            Dispatcher.BeginInvoke(() => ProcessHardwareTouch(touch));
        }
    }

    /// <summary>
    /// One LHS sensor probe (ADR-0004): reads the LibreHardwareService
    /// shared-memory map and caches the snapshot in <see cref="LhmSensorStore"/>
    /// so widgets read it without a WCF round-trip.
    /// </summary>
    private string? _lastSensorError;

    private void SensorPollTick()
    {
        var dto = _lhsReader.Poll();
        if (!ReferenceEquals(_lhsReader.LastError, _lastSensorError))
        {
            _lastSensorError = _lhsReader.LastError;
            if (_lastSensorError != null) Log($"[SENSOR] {_lastSensorError}");
        }
        LhmSensorStore.UpdateFromDto(dto);
    }

    /// <summary>
    /// One frame-time probe: fetches the FPS/frame-time snapshot (targeting the
    /// focused presenter's process) and caches it in <see cref="FrameTimeStore"/>.
    /// </summary>
    private void FrameTimePollTick()
    {
        // Track the foreground window's process so the widget shows the focused
        // game's FPS. When the App itself (or nothing) is focused, pass -1 so
        // the service returns the idle view instead of falling back to the most
        // active presenter.
        int preferredPid = GetForegroundProcessId();
        if (preferredPid <= 0 || preferredPid == Environment.ProcessId)
        {
            preferredPid = -1;
        }

        var dto = _wcfClient?.GetFrameTimeSnapshot(preferredPid);
        FrameTimeStore.UpdateFromDto(dto);
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
    /// Routes a hardware touch sample from the WCF poll into the single input
    /// controller. The type is already normalized to <see cref="TouchEventType"/>
    /// at the service's transport seam — no vendor protocol bytes reach the App.
    /// Display touches are runtime input: routing is never suppressed by the
    /// desktop edit-mode veto (hotkeys fire on the device in edit mode too).
    /// </summary>
    private void ProcessHardwareTouch(TouchEventInfo touch)
    {
        _inputController.Feed(touch.Type, touch.X, touch.Y,
            suppressWidgetRouting: false,
            _profile.Pages.Count, _profile.ActivePageIndex, _profile.ActivePage);
    }

}
