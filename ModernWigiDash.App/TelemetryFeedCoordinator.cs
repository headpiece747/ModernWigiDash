using System;
using ModernWigiDash.App.LibreHardwareService;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Hardware.Service;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App;

/// <summary>
/// Deep telemetry coordinator module (Candidate 2, ADR-0011 exit plan):
/// unifies hardware sensor polling (LibreHardwareService shared memory),
/// frame-time polling (PresentMon ETW), and vendor WCF service access into a
/// single coherent ingestion hub. Implements <see cref="IWidgetTelemetryContext"/>
/// directly to feed <see cref="WidgetHostEnvironment"/> without mandatory static stores.
/// </summary>
internal sealed class TelemetryFeedCoordinator : IWidgetTelemetryContext, IDisposable
{
    private readonly System.Threading.Lock _pollGate = new();
    private readonly IPresentMonNative _presentMonNative;
    private readonly LhmSharedMemoryReader _lhsReader;
    private readonly PresentMonFrameTimeProducer _presentMonProducer;
    private PollLoop _sensorPoll;
    private PollLoop _frameTimePoll;
    private TimeSpan _currentPeriod;
    private bool _running;
    private bool _disposed;
    private readonly Action<string> _log;

    private readonly LogOnChange _sensorErrors = new();
    private readonly LogOnChange _frameTimeErrors = new();
    private readonly LogOnChange _frameTimeDiag = new();

    private readonly TelemetryStoreFacade<SensorSnapshotDto> _sensorStore;
    private readonly TelemetryStoreFacade<FrameTimeSnapshotDto> _frameTimeStore;
    private volatile WigiDashServiceClient? _vendorClient;

    /// <param name="presentMonNative">Runtime PresentMon interop (injectable for testing).</param>
    /// <param name="log">Log line sink.</param>
    /// <param name="lhsMapSource">LHS shared-memory map source (injectable for testing).</param>
    /// <param name="targetResolver">Tracked target resolver (injectable for testing).</param>
    /// <param name="timeProvider">Optional time provider for freshness evaluation.</param>
    /// <param name="vendorClient">Optional initial vendor WCF client.</param>
    /// <param name="initialPeriod">Optional initial polling cadence (defaults to 500ms).</param>
    public TelemetryFeedCoordinator(
        IPresentMonNative presentMonNative,
        Action<string> log,
        ILhmMapSource? lhsMapSource = null,
        TrackedTargetResolver? targetResolver = null,
        TimeProvider? timeProvider = null,
        WigiDashServiceClient? vendorClient = null,
        TimeSpan? initialPeriod = null)
    {
        _log = log;
        _presentMonNative = presentMonNative;
        _currentPeriod = initialPeriod ?? TimeSpan.FromMilliseconds(500);
        _vendorClient = vendorClient;
        _lhsReader = new LhmSharedMemoryReader(lhsMapSource ?? new MemoryMappedLhmMapSource());
        _presentMonProducer = new PresentMonFrameTimeProducer(presentMonNative, targetResolver ?? new TrackedTargetResolver());

        TimeProvider clock = timeProvider ?? TimeProvider.System;
        _sensorStore = new TelemetryStoreFacade<SensorSnapshotDto>(
            new SensorSnapshotDto(),
            defaultMaxAge: TimeSpan.FromSeconds(10),
            lastUpdateOf: dto => dto.LastUpdate);

        _frameTimeStore = new TelemetryStoreFacade<FrameTimeSnapshotDto>(
            new FrameTimeSnapshotDto(),
            defaultMaxAge: TimeSpan.FromSeconds(5),
            lastUpdateOf: dto => dto.LastUpdate);

        if (timeProvider is not null)
        {
            _sensorStore.StoreForTest = _sensorStore.CreateStoreForTest(clock);
            _frameTimeStore.StoreForTest = _frameTimeStore.CreateStoreForTest(clock);
        }

        _sensorPoll = new PollLoop(
            "SENSOR", _currentPeriod, () => true, SensorPollTick, () => { }, log);
        _frameTimePoll = new PollLoop(
            "FRAMETIME", _currentPeriod, () => presentMonNative.IsAvailable, FrameTimePollTick, () => { }, log);
    }

    /// <summary>Starts background telemetry poll loops.</summary>
    public void Start()
    {
        lock (_pollGate)
        {
            _running = true;
            _sensorPoll.Start();
            _frameTimePoll.Start();
        }
    }

    /// <summary>Stops background telemetry poll loops.</summary>
    public void Stop()
    {
        lock (_pollGate)
        {
            _running = false;
            _sensorPoll.Stop();
            _frameTimePoll.Stop();
        }
    }

    /// <summary>
    /// Thread-safely updates the telemetry polling cadence for both sensor and
    /// frame-time background poll loops without restarting the application.
    /// </summary>
    public void UpdatePollingPeriod(TimeSpan newPeriod)
    {
        lock (_pollGate)
        {
            if (_disposed) return;
            if (_currentPeriod == newPeriod) return;

            _currentPeriod = newPeriod;
            _sensorPoll.Dispose();
            _frameTimePoll.Dispose();

            _sensorPoll = new PollLoop(
                "SENSOR", _currentPeriod, () => true, SensorPollTick, () => { }, _log);
            _frameTimePoll = new PollLoop(
                "FRAMETIME", _currentPeriod, () => _presentMonNative.IsAvailable, FrameTimePollTick, () => { }, _log);

            if (_running)
            {
                _sensorPoll.Start();
                _frameTimePoll.Start();
            }

            _log($"[TELEMETRY] Polling cadence updated to {(int)newPeriod.TotalMilliseconds}ms");
        }
    }

    /// <summary>Current active polling period (for diagnostics/tests).</summary>
    public TimeSpan CurrentPollingPeriod
    {
        get { lock (_pollGate) return _currentPeriod; }
    }

    /// <summary>Current vendor WCF service client, or null.</summary>
    public WigiDashServiceClient? VendorClient => _vendorClient;

    /// <summary>
    /// Sets the active vendor service client instance and updates the legacy static fallback.
    /// </summary>
    public void SetVendorService(WigiDashServiceClient? client)
    {
        _vendorClient = client;
        VendorService.SetInstance(client);
    }

    /// <summary>Reads latest sensor snapshot regardless of freshness (e.g. for property dropdowns).</summary>
    public SensorSnapshotDto ReadSensorSnapshot() => _sensorStore.ReadSnapshot();

    /// <summary>Reads latest frame-time snapshot regardless of freshness.</summary>
    public FrameTimeSnapshotDto ReadFrameTimeSnapshot() => _frameTimeStore.ReadSnapshot();

    // ── IWidgetTelemetryContext ─────────────────────────────

    /// <inheritdoc/>
    public SensorSnapshotDto? TryReadSensors() => _sensorStore.TryReadFresh();

    /// <inheritdoc/>
    public FrameTimeSnapshotDto? TryReadFrameTime() => _frameTimeStore.TryReadFresh();

    /// <inheritdoc/>
    public object? TryGetVendorService() => _vendorClient;

    // ── Polling Ticks ───────────────────────────────────────

    /// <summary>Executes one LHS sensor probe.</summary>
    internal void SensorPollTick()
    {
        var dto = _lhsReader.Poll();
        string? error = _lhsReader.LastError;
        if (_sensorErrors.Changed(error) && error != null) _log($"[SENSOR] {error}");

        _sensorStore.UpdateFromDto(dto);
        LhmSensorStore.UpdateFromDto(dto);
    }

    /// <summary>Executes one PresentMon frame-time probe.</summary>
    internal void FrameTimePollTick()
    {
        var dto = _presentMonProducer.Poll();
        string? error = !dto.IsAvailable || !dto.CaptureHealthy ? dto.ErrorMessage : null;
        if (_frameTimeErrors.Changed(error) && error != null)
        {
            _log($"[FRAMETIME] frame capture unavailable: {error}");
        }

        var diag = _presentMonProducer.LastDiagnostics;
        string line = $"fg='{diag.ForegroundTitle}' candidates=[{string.Join(',', diag.Candidates)}] liveRoot={diag.LiveRootPid} streak={diag.ForeignStreak} guard={diag.CheckingAdopted} outcome={diag.Outcome} → pid={dto.ProcessId} fps={dto.Fps:F1} disp={dto.DisplayedFps:F1} avail={dto.IsAvailable} healthy={dto.CaptureHealthy}";
        if (_frameTimeDiag.Changed(line))
        {
            _log($"[FRAMETIME-DIAG] {line}");
        }

        _frameTimeStore.UpdateFromDto(dto);
        FrameTimeStore.UpdateFromDto(dto);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_pollGate)
        {
            if (_disposed) return;
            _disposed = true;
            _sensorPoll.Dispose();
            _frameTimePoll.Dispose();
            _presentMonProducer.Dispose();
        }
    }
}
