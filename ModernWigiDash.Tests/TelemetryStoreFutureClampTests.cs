using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// The store's future-timestamp clamp: a producer clock ahead of the store
/// must not pin snapshots fresh forever.
/// </summary>
[TestClass]
public class TelemetryStoreFutureClampTests
{
    private sealed class FakeTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    }

    [TestMethod]
    public void Update_FutureProducerTimestamp_ClampsToReceiveTime()
    {
        var clock = new FakeTime();
        var store = new TelemetryStore<string>("", TimeSpan.FromSeconds(5), clock);

        store.Update("fresh", producerTimestamp: clock.GetUtcNow().UtcDateTime.AddHours(1));

        Assert.AreEqual("fresh", store.Current, "the record is stored");
        Assert.AreEqual("fresh", store.TryReadFresh(TimeSpan.FromSeconds(5), clock),
            "the clamped record is fresh at the store's own time");
    }

    [TestMethod]
    public void Update_PastProducerTimestamp_GoesStaleAfterWindow()
    {
        var clock = new FakeTime();
        var store = new TelemetryStore<string>("", TimeSpan.FromSeconds(5), clock);

        store.Update("old", producerTimestamp: clock.GetUtcNow().UtcDateTime.AddSeconds(-6));

        Assert.IsNull(store.TryReadFresh(TimeSpan.FromSeconds(5), clock),
            "a past producer timestamp governs staleness normally");
        Assert.AreEqual("old", store.Current);
    }
}
