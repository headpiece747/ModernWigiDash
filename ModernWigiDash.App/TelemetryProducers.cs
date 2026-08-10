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
    private readonly LhmSharedMemoryReader _lhsReader;
    private readonly PresentMonFrameTimeProducer _presentMonProducer;
    private readonly PollLoop _sensorPoll;
    private readonly PollLoop _frameTimePoll;
    private readonly Action<string> _log;

    // Message-change dedup for the two error surfaces — one rule, two uses
    // (the old code mirrored the same comparison in both tick bodies).
    private readonly LogOnChange _sensorErrors = new();
    private readonly LogOnChange _frameTimeErrors = new();

    /// <param name="presentMonNative">The runtime-loaded PresentMon interop
    /// (injected so tests never load the real DLL).</param>
    /// <param name="log">Log sink (the window's FileLog line writer).</param>
    /// <param name="lhsReader">The LHS map reader (injectable so tests drive
    /// the poll tick with an in-memory map source).</param>
    /// <param name="targetResolver">The PresentMon tracking-target resolver
    /// (injectable so tests can stub the foreground window).</param>
    public TelemetryProducers(
        IPresentMonNative presentMonNative,
        Action<string> log,
        LhmSharedMemoryReader? lhsReader = null,
        TrackedTargetResolver? targetResolver = null)
    {
        _log = log;
        _lhsReader = lhsReader ?? new LhmSharedMemoryReader();
        _presentMonProducer = new PresentMonFrameTimeProducer(presentMonNative, targetResolver ?? new TrackedTargetResolver());

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
        string? error = _lhsReader.LastError;
        if (_sensorErrors.Changed(error) && error != null) _log($"[SENSOR] {error}");
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
        string? error = !dto.IsAvailable || !dto.CaptureHealthy ? dto.ErrorMessage : null;
        if (_frameTimeErrors.Changed(error) && error != null)
        {
            _log($"[FRAMETIME] frame capture unavailable: {error}");
        }

        FrameTimeStore.UpdateFromDto(dto);
    }

    public void Dispose()
    {
        Stop();
        _presentMonProducer.Dispose();
    }
}
