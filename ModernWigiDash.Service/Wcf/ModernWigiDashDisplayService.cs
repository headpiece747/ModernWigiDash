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
/// Frame writes are routed through the Channel&lt;byte[]&gt; so that
/// DisplayHardwareWorkerService is the sole USB writer (avoids concurrent access).
/// </summary>
[CoreWCF.ServiceBehavior(IncludeExceptionDetailInFaults = false)]
public class ModernWigiDashDisplayService : ModernWigiDashDisplayServiceContract
{
    private readonly DisplayHidTransport _transport;
    private readonly DisplayHardwareWorkerService? _hardwareWorker;
    private readonly ChannelWriter<byte[]> _frameChannelWriter;
    private readonly ChannelReader<DisplayTouchInput> _touchReader;
    private readonly LhmSensorReader? _lhmSensorReader;
    private readonly FrameTimeReader? _frameTimeReader;
    private readonly ILogger<ModernWigiDashDisplayService> _logger;
    private readonly DateTime _startTime = DateTime.UtcNow;
    private static readonly string VersionString = typeof(ModernWigiDashDisplayService)
        .Assembly.GetName().Version?.ToString() ?? "1.0.0.0";

    public ModernWigiDashDisplayService(
        DisplayHidTransport transport,
        DisplayHardwareWorkerService? hardwareWorker,
        ChannelWriter<byte[]> frameChannelWriter,
        ChannelReader<DisplayTouchInput> touchReader,
        ILogger<ModernWigiDashDisplayService> logger,
        LhmSensorReader? lhmSensorReader = null,
        FrameTimeReader? frameTimeReader = null)
    {
        _transport = transport;
        _hardwareWorker = hardwareWorker;
        _frameChannelWriter = frameChannelWriter;
        _touchReader = touchReader;
        _lhmSensorReader = lhmSensorReader;
        _frameTimeReader = frameTimeReader;
        _logger = logger;

        LogToFile("[WCF] ModernWigiDashDisplayService constructor called");
        _logger.LogInformation("CoreWCF: ModernWigiDashDisplayService instantiated");
    }

    public bool InitializeDisplay()
    {
        try
        {
            _logger.LogInformation("CoreWCF: InitializeDisplay requested");
#pragma warning disable S6966 // WCF contract methods cannot be async; async would require interface change
            bool connected;
            if (_transport.IsConnected)
            {
                // The display may have been switched to the vendor welcome screen by a prior
                // Shutdown(). Re-run the full init sequence on the existing connection so the
                // display returns to Base0 with all pages configured.
                bool reinitOk = _transport.SendInitCommandsAsync().GetAwaiter().GetResult();
                if (reinitOk)
                {
                    connected = true;
                }
                else
                {
                    // Existing handles are unresponsive (e.g. device physically re-enumerated).
                    // Tear down and fully reconnect so ConnectAsync re-initializes the device.
                    LogToFile("[WCF] Re-init on existing connection failed — forcing full reconnect");
                    _transport.DisconnectAsync().GetAwaiter().GetResult();
                    connected = _transport.ConnectAsync().GetAwaiter().GetResult();
                }
            }
            else
            {
                connected = _transport.ConnectAsync().GetAwaiter().GetResult();
            }
#pragma warning restore S6966
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
            _logger.LogInformation("CoreWCF: DeInitializeDisplay requested");
#pragma warning disable S6966 // WCF contract methods cannot be async
            _transport.DisconnectAsync().GetAwaiter().GetResult();
#pragma warning restore S6966
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
            long frames = _hardwareWorker?.TotalFramesProcessed ?? 0;
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
            byte clamped = (byte)Math.Clamp((int)brightnessPercent, 0, 100);
            _logger.LogInformation("CoreWCF: SetBrightness to {Brightness}", clamped);
#pragma warning disable S6966 // WCF contract methods cannot be async
            bool result = _transport.SetBrightnessAsync(clamped).AsTask().GetAwaiter().GetResult();
#pragma warning restore S6966
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
            LogToFile($"[WCF] SendFrame called with payload size: {payload?.Data?.Length ?? 0}");

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

            // Route through the channel so DisplayHardwareWorkerService is the sole USB writer.
            // DropOldest policy ensures we always send the most recent frame (no buildup).
            byte[] frameCopy = payload.Data.ToArray();
            bool queued = _frameChannelWriter.TryWrite(frameCopy);
            if (queued)
            {
                _logger.LogDebug("CoreWCF: SendFrame queued ({Size} bytes)", frameCopy.Length);
                LogToFile($"[WCF] SendFrame queued: {frameCopy.Length} bytes");
            }
            else
            {
                _logger.LogWarning("CoreWCF: SendFrame channel full — dropped frame ({Size} bytes)", frameCopy.Length);
                LogToFile($"[WCF] SendFrame DROPPED: channel full ({frameCopy.Length} bytes)");
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
            var uptime = DateTime.UtcNow - _startTime;
            return new ServiceDiagnostics
            {
                ServiceName = "ModernWigiDashService",
                ServiceAccount = "[redacted]",
                Uptime = uptime.ToString(@"hh\:mm\:ss\.fff"),
                DisplayStatus = $"IsConnected={_transport.IsConnected} | Path={_transport.DevicePath}",
                WcfEndpoint = "http://localhost:8733/",
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
            if (_touchReader.TryRead(out var touch))
            {
                return new TouchEventInfo
                {
                    Type = touch.Type,
                    X = touch.X,
                    Y = touch.Y,
                    TimestampUtcTicks = touch.Timestamp.Ticks
                };
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
            _logger.LogInformation("CoreWCF: Shutdown requested — resetting display to welcome screen");
#pragma warning disable S6966, S5034 // WCF contract methods cannot be async; ValueTask consumed intentionally
            _transport.ClearPageAsync(0).AsTask().GetAwaiter().GetResult();
            _transport.GoToScreenAsync(0x01).AsTask().GetAwaiter().GetResult();
#pragma warning restore S6966, S5034
            return true;
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
            return new SensorSnapshotDto { IsConnected = false, LastUpdate = DateTime.UtcNow, Readings = [] };
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
            return new FrameTimeSnapshotDto { IsAvailable = false, ErrorMessage = ex.Message, LastUpdate = DateTime.UtcNow };
        }
    }

    private static void LogToFile(string message)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "display_device.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("LogToFile failed; ignoring logging error");
            // Ignore logging failures
        }
    }
}
