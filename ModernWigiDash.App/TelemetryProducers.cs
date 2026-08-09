using ModernWigiDash.App.LibreHardwareService;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

/// <summary>
/// Owns the telemetry producer cluster: the two direct poll loops — sensors
/// via LibreHardwareService shared memory (ADR-0004), frame-time via the
/// PresentMon Service (ADR-0003) — with their producer, reader, and
/// error-dedup state. The window keeps one Start() and one Stop(); the whole
/// cluster is testable without WPF (previously, exercising this wiring meant
/// constructing the entire window).
/// </summary>
public sealed class TelemetryProducers : IDisposable
{
    private readonly LhmSharedMemoryReader _lhsReader = new();
    private readonly PresentMonFrameTimeProducer _presentMonProducer;
    private readonly PollLoop _sensorPoll;
    private readonly PollLoop _frameTimePoll;
    private readonly Action<string> _log;

    private string? _lastSensorError;
    private string? _lastFrameTimeError;

    /// <param name="presentMonNative">The runtime-loaded PresentMon interop
    /// (injected so tests never load the real DLL).</param>
    /// <param name="log">Log sink (the window's FileLog line writer).</param>
    public TelemetryProducers(IPresentMonNative presentMonNative, Action<string> log)
    {
        _log = log;
        _presentMonProducer = new PresentMonFrameTimeProducer(presentMonNative, new TrackedTargetResolver());

        // One poll loop per direct producer. The frame-time loop is gated on
        // the runtime-loaded API library being available; the producer owns
        // its tracking-target resolution (foreground process + descendants).
        _sensorPoll = new PollLoop(
            "SENSOR", TimeSpan.FromSeconds(1), () => true, SensorPollTick, () => { }, log);
        _frameTimePoll = new PollLoop(
            "FRAMETIME", TimeSpan.FromSeconds(1), () => presentMonNative.IsAvailable, FrameTimePollTick, () => { }, log);
    }

    public void Start()
    {
        _sensorPoll.Start();
        _frameTimePoll.Start();
    }

    public void Stop()
    {
        _sensorPoll.Stop();
        _frameTimePoll.Stop();
    }

    /// <summary>
    /// One LHS sensor probe (ADR-0004): reads the LibreHardwareService
    /// shared-memory map and caches the snapshot in <see cref="LhmSensorStore"/>
    /// so widgets read it without a service round-trip.
    /// </summary>
    internal void SensorPollTick()
    {
        var dto = _lhsReader.Poll();
        if (_lhsReader.LastError != _lastSensorError)
        {
            _lastSensorError = _lhsReader.LastError;
            if (_lastSensorError != null) _log($"[SENSOR] {_lastSensorError}");
        }
        LhmSensorStore.UpdateFromDto(dto);
    }

    /// <summary>
    /// One PresentMon probe (ADR-0003): polls the frame-time producer and
    /// caches the snapshot in <see cref="FrameTimeStore"/> for widgets to read.
    /// </summary>
    internal void FrameTimePollTick()
    {
        var dto = _presentMonProducer.Poll();
        // Surface capture-health failures (service ETW dead) alongside the
        // unavailable state, once per message change.
        if (!dto.IsAvailable || !dto.CaptureHealthy)
        {
            if (dto.ErrorMessage != _lastFrameTimeError)
            {
                _lastFrameTimeError = dto.ErrorMessage;
                _log($"[FRAMETIME] frame capture unavailable: {dto.ErrorMessage}");
            }
        }
        else
        {
            _lastFrameTimeError = null;
        }

        FrameTimeStore.UpdateFromDto(dto);
    }

    public void Dispose()
    {
        Stop();
        _presentMonProducer.Dispose();
    }
}
