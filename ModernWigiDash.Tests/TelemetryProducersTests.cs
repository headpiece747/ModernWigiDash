using ModernWigiDash.App;
using ModernWigiDash.App.LibreHardwareService;
using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The telemetry producer cluster — the two poll-loop tick bodies and the
/// error-dedup policy, previously only reachable by constructing the whole
/// WPF window.
/// </summary>
[TestClass]
public class TelemetryProducersTests
{

    [TestMethod]
    public void Start_Stop_Dispose_IsSafe()
    {
        using var producers = new TelemetryProducers(new StubPresentMonNative(), _ => { });

        producers.Start();
        producers.Start(); // idempotent — must not throw
        producers.Stop();
        producers.Dispose();
        producers.Dispose(); // idempotent — must not throw

        Assert.IsNotNull(producers);
    }

    [TestMethod]
    public void FrameTimePollTick_Unavailable_DedupsErrorLogging()
    {
        var logs = new List<string>();
        using var producers = new TelemetryProducers(new StubPresentMonNative(), logs.Add);

        producers.FrameTimePollTick();
        producers.FrameTimePollTick();

        Assert.AreEqual(1, logs.Count(log => log.Contains("[FRAMETIME]", StringComparison.Ordinal)),
            "The same unavailable message must be logged once per change, not per tick");
    }

    [TestMethod]
    public void SensorPollTick_WithoutMaps_DoesNotThrow()
    {
        var logs = new List<string>();
        using var producers = new TelemetryProducers(new StubPresentMonNative(), logs.Add);

        // No LibreHardwareService maps exist in the test host: the reader
        // reports nothing to log, the store is updated with an unavailable
        // snapshot, and the tick must not throw.
        producers.SensorPollTick();
        producers.SensorPollTick();

        Assert.AreEqual(0, logs.Count(log => log.Contains("[SENSOR]", StringComparison.Ordinal)),
            "A silent poll must not spam the log");
    }

    // ── success ticks through the cluster seams (moved from the
    // residual-coverage grab-bag) ──

    [TestMethod]
    public void FrameTimePollTick_Success_UpdatesStore()
    {
        using var producers = new TelemetryProducers(
            new StubPresentMonNative
            {
                IsAvailable = true,
                OpenSessionResult = true,
                PollResult = new PresentMonDynamicSample(120.0, 100.0, 1.0, 3.0, 119.0, 0, 2.0, 4),
            },
            _ => { },
            targetResolver: new TrackedTargetResolver(() => 4321, _ => []));
        FrameTimeStore.Reset();

        producers.FrameTimePollTick();

        var fresh = FrameTimeStore.TryReadFresh(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(fresh, "a live sample through the cluster must land in the store");
        Assert.AreEqual(120.0, fresh.Fps, 0.001);
        FrameTimeStore.Reset();
    }

    [TestMethod]
    public void SensorPollTick_Success_UpdatesStore()
    {
        byte[] map = LhmSharedMemoryReaderTests.BuildSensorsMapFixture();
        using var producers = new TelemetryProducers(
            new StubPresentMonNative(),
            _ => { },
            lhsReader: new LhmSharedMemoryReader(new StubLhmMapSource(map)));
        LhmSensorStore.Reset();

        producers.SensorPollTick();

        var snap = LhmSensorStore.ReadSnapshot();
        Assert.IsTrue(snap.IsConnected, "a live sensor map through the cluster must connect the store");
        Assert.IsTrue(snap.Readings.Count > 0);
        LhmSensorStore.Reset();
    }
}
