using Microsoft.Extensions.Logging.Abstractions;
using ModernWigiDash.Service.Services;

namespace ModernWigiDash.Tests;

[TestClass]
public class FrameTimeReaderTests
{
    private sealed class FakeClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [TestMethod]
    public void GetSnapshot_StampsSnapshotProductionTime()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var reader = new FrameTimeReader(NullLogger<FrameTimeReader>.Instance, clock);

        var snapshot = reader.GetSnapshot(0);

        // The freshness contract: a snapshot is fresh while the reader is
        // alive, regardless of whether any process is currently presenting.
        Assert.AreEqual(clock.GetUtcNow().UtcDateTime, snapshot.LastUpdate);
    }

    [TestMethod]
    public void GetSnapshot_WhenNotRunning_IsNotAvailable()
    {
        var reader = new FrameTimeReader(NullLogger<FrameTimeReader>.Instance, new FakeClock(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero)));

        var snapshot = reader.GetSnapshot(0);

        Assert.IsFalse(snapshot.IsAvailable, "A reader that has not started must report unavailable");
    }
}
