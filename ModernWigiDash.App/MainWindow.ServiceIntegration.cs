using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

/// <summary>
/// MainWindow partial: direct telemetry poll loops (sensors via LibreHardwareService
/// shared memory, frame-time via PresentMon Service). Frames and touch go straight
/// to the USB engine — the Windows Service is gone (ADR-0005).
/// </summary>
public partial class MainWindow
{
    /// <summary>
    /// One LHS sensor probe (ADR-0004): reads the LibreHardwareService
    /// shared-memory map and caches the snapshot in <see cref="LhmSensorStore"/>
    /// so widgets read it without a service round-trip.
    /// </summary>
    private string? _lastSensorError;

    private void SensorPollTick()
    {
        var dto = _lhsReader.Poll();
        if (_lhsReader.LastError != _lastSensorError)
        {
            _lastSensorError = _lhsReader.LastError;
            if (_lastSensorError != null) Log($"[SENSOR] {_lastSensorError}");
        }
        LhmSensorStore.UpdateFromDto(dto);
    }

    /// <summary>
    /// One PresentMon probe (ADR-0003): polls the frame-time producer and caches
    /// the snapshot in <see cref="FrameTimeStore"/> for widgets to read.
    /// </summary>
    private string? _lastFrameTimeError;

    private void FrameTimePollTick()
    {
        var dto = _presentMonProducer.Poll();
        // Surface capture-health failures (service ETW dead) alongside the
        // unavailable state, once per message change.
        if (!dto.IsAvailable || !dto.CaptureHealthy)
        {
            // The widget cannot render the reason — surface it once per change.
            if (dto.ErrorMessage != _lastFrameTimeError)
            {
                _lastFrameTimeError = dto.ErrorMessage;
                Log($"[FRAMETIME] frame capture unavailable: {dto.ErrorMessage}");
            }
        }
        else
        {
            _lastFrameTimeError = null;
        }

        FrameTimeStore.UpdateFromDto(dto);
    }
}
