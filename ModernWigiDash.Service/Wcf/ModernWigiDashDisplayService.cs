using ModernWigiDash.Sdk;
using System.Threading.Channels;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Service.Contracts;
using ModernWigiDash.Service.Services;

namespace ModernWigiDash.Service.Wcf;

/// <summary>
/// CoreWCF service implementation for ModernWigiDash display control.
/// Provides display operations over HTTP.
///
/// When running as a Windows Service with LocalSystem account, this service
/// has full access to USB HID/WinUSB devices required for the WigiDash display.
///
/// Frame writes are pushed into the shared <see cref="FrameDelivery"/> module,
/// whose single policy (DropOldest → drain-to-latest → paced send) owns the
/// service hop exactly as it owns the App's WCF and direct-USB hops.
/// </summary>
[CoreWCF.ServiceBehavior(IncludeExceptionDetailInFaults = false)]
public class ModernWigiDashDisplayService : IModernWigiDashDisplayServiceContract
{
    private readonly IDisplayTransport _transport;
    private readonly FrameDelivery _frameDelivery;
    private readonly ChannelReader<TouchEventInfo> _touchReader;
    private readonly LhmSensorReader? _lhmSensorReader;
    private readonly FrameTimeReader? _frameTimeReader;
    private readonly ILogger<ModernWigiDashDisplayService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly DateTime _startTime;
    private static readonly string VersionString = typeof(ModernWigiDashDisplayService)
        .Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

    public ModernWigiDashDisplayService(
        IDisplayTransport transport,
        FrameDelivery frameDelivery,
        ChannelReader<TouchEventInfo> touchReader,
        ILogger<ModernWigiDashDisplayService> logger,
        LhmSensorReader? lhmSensorReader = null,
        FrameTimeReader? frameTimeReader = null,
        TimeProvider? timeProvider = null)
    {
        _transport = transport;
        _frameDelivery = frameDelivery;
        _touchReader = touchReader;
        _lhmSensorReader = lhmSensorReader;
        _frameTimeReader = frameTimeReader;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _startTime = _timeProvider.GetUtcNow().UtcDateTime;

        LogToFile("[WCF] ModernWigiDashDisplayService constructor called");
        _logger.LogInformation("CoreWCF: ModernWigiDashDisplayService instantiated");
    }

    public bool InitializeDisplay()
    {
        try
        {
            AuditCall(nameof(InitializeDisplay));
            _logger.LogInformation("CoreWCF: InitializeDisplay requested");
            bool connected;
            if (_transport.IsConnected)
            {
                // The display may have been switched to the vendor welcome screen by a prior
                // Shutdown(). Re-run the full init sequence on the existing connection so the
                // display returns to Base0 with all pages configured.
                bool reinitOk = _transport.SendInitCommands();
                if (reinitOk)
                {
                    connected = true;
                }
                else
                {
                    // Existing handles are unresponsive (e.g. device physically re-enumerated).
                    // Tear down and fully reconnect so Connect re-initializes the device.
                    LogToFile("[WCF] Re-init on existing connection failed — forcing full reconnect");
                    _transport.Disconnect();
                    connected = _transport.Connect();
                }
            }
            else
            {
                connected = _transport.Connect();
            }
            _logger.LogInformation("CoreWCF: InitializeDisplay result: {Connected}", connected);
            return connected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoreWCF: InitializeDisplay failed");
            return false;
        }
    }

    public bool DeInitializeDisplay()
    {
        try
        {
            AuditCall(nameof(DeInitializeDisplay));
            _logger.LogInformation("CoreWCF: DeInitializeDisplay requested");
            _transport.Disconnect();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoreWCF: DeInitializeDisplay failed");
            return false;
        }
    }

    public DisplayStatus GetDisplayStatus()
    {
        try
        {
            long frames = _frameDelivery.FramesSent;
            return new DisplayStatus
            {
                IsConnected = _transport.IsConnected,
                DevicePath = _transport.DevicePath,
                State = _transport.IsConnected ? "Connected" : "Disconnected",
                TotalFramesProcessed = frames,
                DiagnosticSummary = $"IsConnected={_transport.IsConnected} | Path={_transport.DevicePath} | Frames={frames}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoreWCF: GetDisplayStatus failed");
            return new DisplayStatus
            {
                IsConnected = false,
                State = "Error",
                DiagnosticSummary = ex.Message
            };
        }
    }

    public bool SetBrightness(byte brightnessPercent)
    {
        try
        {
            AuditCall(nameof(SetBrightness));
            byte clamped = (byte)Math.Clamp((int)brightnessPercent, 0, 100);
            _logger.LogInformation("CoreWCF: SetBrightness to {Brightness}", clamped);
            bool result = _transport.SetBrightness(clamped);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoreWCF: SetBrightness failed");
            return false;
        }
    }

    public bool SendFrame(FramePayload payload)
    {
        try
        {
            AuditCall(nameof(SendFrame));
            if (payload == null || payload.Data == null || payload.Data.Length == 0)
            {
                _logger.LogWarning("CoreWCF: SendFrame received null or empty FramePayload");
                LogToFile("[WCF] SendFrame rejected: null or empty payload");
                return false;
            }

            int maxSize = DisplayProtocolConstants.FrameBufferSize * 2;
            if (payload.Data.Length > maxSize)
            {
                _logger.LogWarning("CoreWCF: SendFrame rejected oversized payload ({Size} bytes, max {Max})", payload.Data.Length, maxSize);
                LogToFile($"[WCF] SendFrame rejected: oversized ({payload.Data.Length} bytes)");
                return false;
            }

            // Push through the shared delivery module so the service hop obeys
            // the same DropOldest → drain-to-latest policy as every other hop.
            bool queued = _frameDelivery.PushBytes(payload.Data.ToArray()) == ModernWigiDash.Sdk.FrameDeliveryResult.Queued;
            if (!queued)
            {
                _logger.LogWarning("CoreWCF: SendFrame rejected ({Size} bytes)", payload.Data.Length);
                LogToFile($"[WCF] SendFrame DROPPED: pipeline rejected ({payload.Data.Length} bytes)");
            }
            return queued;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoreWCF: SendFrame failed");
            LogToFile($"[WCF] SendFrame exception: {ex.Message}");
            return false;
        }
    }

    public ServiceDiagnostics GetDiagnostics()
    {
        try
        {
            var uptime = _timeProvider.GetUtcNow().UtcDateTime - _startTime;
            return new ServiceDiagnostics
            {
                ServiceName = "ModernWigiDashService",
                ServiceAccount = "[redacted]",
                Uptime = uptime.ToString(@"hh\:mm\:ss\.fff"),
                DisplayStatus = $"IsConnected={_transport.IsConnected} | Path={_transport.DevicePath}",
                WcfEndpoint = "net.pipe://localhost/ModernWigiDashDisplayService/WigiDash.svc",
                Version = VersionString
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoreWCF: GetDiagnostics failed");
            return new ServiceDiagnostics
            {
                ServiceName = "ModernWigiDashService",
                DisplayStatus = ex.Message,
                Version = VersionString
            };
        }
    }

    public string GetVersion()
    {
        LogToFile("[WCF] GetVersion called");
        return VersionString;
    }

    public TouchEventInfo? PollTouch()
    {
        try
        {
            // The worker normalizes the raw protocol byte to TouchEventType at
            // the transport seam, so this hop is a pure pass-through.
            if (_touchReader.TryRead(out var touch))
            {
                return touch;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoreWCF: PollTouch failed");
            return null;
        }
    }

    public bool Shutdown()
    {
        try
        {
            AuditCall(nameof(Shutdown));
            _logger.LogInformation("CoreWCF: Shutdown requested — putting display into standby");
            bool standby = _transport.GoToStandby();
            if (!standby)
            {
                LogToFile("[WCF] Shutdown: transport not connected; standby skipped");
            }
            return standby;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoreWCF: Shutdown failed");
            return false;
        }
    }

    public SensorSnapshotDto GetSensorSnapshot()
    {
        try
        {
            var snapshot = _lhmSensorReader?.GetSnapshot() ?? new SensorSnapshotDto { IsConnected = false };
            if (!snapshot.IsConnected)
            {
                LogToFile("[WCF] GetSensorSnapshot: LHM unavailable (needs admin/SYSTEM context)");
            }
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoreWCF: GetSensorSnapshot failed");
            return new SensorSnapshotDto { IsConnected = false, LastUpdate = _timeProvider.GetUtcNow().UtcDateTime, Readings = [] };
        }
    }

    public FrameTimeSnapshotDto GetFrameTimeSnapshot(int preferredProcessId = 0)
    {
        try
        {
            var snapshot = _frameTimeReader?.GetSnapshot(preferredProcessId) ?? new FrameTimeSnapshotDto
            {
                IsAvailable = false,
                ErrorMessage = "Frame time capture is not initialized."
            };
            if (!snapshot.IsAvailable)
            {
                LogToFile($"[WCF] GetFrameTimeSnapshot: {snapshot.ErrorMessage}");
            }
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CoreWCF: GetFrameTimeSnapshot failed");
            return new FrameTimeSnapshotDto { IsAvailable = false, ErrorMessage = ex.Message, LastUpdate = _timeProvider.GetUtcNow().UtcDateTime };
        }
    }

    private static void LogToFile(string message) => FileLog.Write(message);

    /// <summary>
    /// Records an audit entry for a mutating operation: which principal invoked
    /// it and from where. The service runs as LocalSystem, so every state change
    /// (InitializeDisplay, SetBrightness, SendFrame, Shutdown, ...) must leave
    /// a trace of the requesting caller.
    /// </summary>
    private static void AuditCall(string operation)
    {
        try
        {
            string? caller = System.Security.Principal.WindowsIdentity.GetCurrent()?.Name;
            string? remote = CoreWCF.OperationContext.Current?.IncomingMessageProperties
                .TryGetValue(CoreWCF.Channels.RemoteEndpointMessageProperty.Name, out var ep) == true
                ? ((CoreWCF.Channels.RemoteEndpointMessageProperty)ep).Address
                : null;
            FileLog.Write($"[WCF-AUDIT] {operation} by {(caller ?? "unknown")} from {(remote ?? "unknown")}");
        }
        catch
        {
            // Audit logging must never break the operation.
            System.Diagnostics.Debug.WriteLine($"WCF audit entry failed for {operation}");
        }
    }
}
