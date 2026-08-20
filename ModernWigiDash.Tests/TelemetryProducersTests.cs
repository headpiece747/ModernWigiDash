using ModernWigiDash.App;
using ModernWigiDash.App.PresentMon;
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
        List<string> logs = [];
        using var producers = new TelemetryProducers(new StubPresentMonNative(), logs.Add);

        producers.FrameTimePollTick();
        producers.FrameTimePollTick();

        Assert.AreEqual(1, logs.Count(log => log.Contains("[FRAMETIME]", StringComparison.Ordinal)),
            "The same unavailable message must be logged once per change, not per tick");
    }

    [TestMethod]
    public void SensorPollTick_WithoutMaps_DoesNotThrow()
    {
        List<string> logs = [];
        using var producers = new TelemetryProducers(
            new StubPresentMonNative(),
            logs.Add,
            lhsMapSource: new StubLhmMapSource());

        // A stub with no bytes and no error stands in for a host with no
        // LibreHardwareService maps: the reader's missing-map state is one
        // stable error value, so the error-dedup rule logs it once across
        // ticks and the store is updated with an unavailable snapshot. The
        // tick must not throw. The source is injected so this holds on any
        // runner — without it the real shared-memory source makes the test
        // depend on whether the LHS service happens to be running.
        producers.SensorPollTick();
        producers.SensorPollTick();

        Assert.AreEqual(1, logs.Count(log => log.Contains("[SENSOR]", StringComparison.Ordinal)),
            "The missing-map state must be logged once per change, not per tick");
    }

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

        var fresh = FrameTimeStore.TryReadFresh();
        Assert.IsNotNull(fresh, "a live sample through the cluster must land in the store");
        Assert.AreEqual(120.0, fresh.Fps, 0.001);
        FrameTimeStore.Reset();
    }

    [TestMethod]
    public void FrameTimePollTick_Success_LogsDiagnosticsWithOutcomeAndTitle()
    {
        // The diagnostics line is the transition evidence — the outcome kind
        // must distinguish Live from the settling/frozen Idle shapes, and the
        // resolver's foreground title must reach the log.
        List<string> logs = [];
        using var producers = new TelemetryProducers(
            new StubPresentMonNative
            {
                IsAvailable = true,
                OpenSessionResult = true,
                PollResult = new PresentMonDynamicSample(120.0, 100.0, 1.0, 3.0, 119.0, 0, 2.0, 4),
            },
            logs.Add,
            targetResolver: new TrackedTargetResolver(() => 4321, _ => [], () => "My Game Window"));

        producers.FrameTimePollTick();

        string? line = logs.FirstOrDefault(l => l.Contains("[FRAMETIME-DIAG]", StringComparison.Ordinal));
        Assert.IsNotNull(line, "a poll must produce one diagnostics line");
        StringAssert.Contains(line, "fg='My Game Window'", "the resolver's title must reach the log");
        StringAssert.Contains(line, "outcome=Live", "the outcome kind must be logged (settling vs frozen distinguishable)");
        StringAssert.Contains(line, "liveRoot=4321", "the trusted target must be logged");
    }

    [TestMethod]
    public void SensorPollTick_Success_UpdatesStore()
    {
        byte[] map = LhmSharedMemoryReaderTests.BuildSensorsMapFixture();
        using var producers = new TelemetryProducers(
            new StubPresentMonNative(),
            _ => { },
            lhsMapSource: new StubLhmMapSource { Bytes = map });
        LhmSensorStore.Reset();

        producers.SensorPollTick();

        var snap = LhmSensorStore.ReadSnapshot();
        Assert.IsTrue(snap.IsConnected, "a live sensor map through the cluster must connect the store");
        Assert.IsTrue(snap.Readings.Count > 0);
        LhmSensorStore.Reset();
    }
}
