
namespace ModernWigiDash.Tests;

/// <summary>
/// The store's future-timestamp clamp: a producer clock ahead of the store
/// must not pin snapshots fresh forever.
/// </summary>
[TestClass]
public class TelemetryStoreFutureClampTests
{
    [TestMethod]
    public void Update_FutureProducerTimestamp_ClampsToReceiveTime()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var store = new TelemetryStore<string>("", TimeSpan.FromSeconds(5), clock);

        store.Update("fresh", producerTimestamp: clock.GetUtcNow().UtcDateTime.AddHours(1));

        Assert.AreEqual("fresh", store.Current, "the record is stored");
        Assert.AreEqual("fresh", store.TryReadFresh(),
            "the clamped record is fresh at the store's own time");
    }

    [TestMethod]
    public void Update_PastProducerTimestamp_GoesStaleAfterWindow()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero));
        var store = new TelemetryStore<string>("", TimeSpan.FromSeconds(5), clock);

        store.Update("old", producerTimestamp: clock.GetUtcNow().UtcDateTime.AddSeconds(-6));

        Assert.IsNull(store.TryReadFresh(),
            "a past producer timestamp governs staleness normally");
        Assert.AreEqual("old", store.Current);
    }
}
