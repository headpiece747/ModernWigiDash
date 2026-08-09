using ModernWigiDash.App;

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
}
