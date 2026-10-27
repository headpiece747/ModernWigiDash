using System;
using ModernWigiDash.App.LibreHardwareService;
using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.App;

/// <summary>
/// Telemetry producer cluster (ADR-0003/0004): retained as a specialized facade over
/// <see cref="TelemetryFeedCoordinator"/> for backward compatibility.
/// </summary>
internal sealed class TelemetryProducers : IDisposable
{
    private readonly TelemetryFeedCoordinator _coordinator;

    public TelemetryProducers(
        IPresentMonNative presentMonNative,
        Action<string> log,
        ILhmMapSource? lhsMapSource = null,
        TrackedTargetResolver? targetResolver = null)
    {
        _coordinator = new TelemetryFeedCoordinator(presentMonNative, log, lhsMapSource, targetResolver);
    }

    public void Start() => _coordinator.Start();

    public void Stop() => _coordinator.Stop();

    public void Dispose() => _coordinator.Dispose();

    internal void SensorPollTick() => _coordinator.SensorPollTick();

    internal void FrameTimePollTick() => _coordinator.FrameTimePollTick();

    internal TelemetryFeedCoordinator Coordinator => _coordinator;
}
