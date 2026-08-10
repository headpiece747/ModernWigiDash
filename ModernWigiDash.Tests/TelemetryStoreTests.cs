using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class TelemetryStoreTests
{
    private sealed record SampleSnapshot(bool Connected, DateTime LastUpdate, IReadOnlyList<string> Items)
    {
        public static SampleSnapshot Empty() => new(false, DateTime.MinValue, []);
    }

    private static TelemetryStore<SampleSnapshot> CreateStore(TimeSpan defaultMaxAge, TimeProvider? timeProvider = null)
        => new(SampleSnapshot.Empty(), defaultMaxAge, timeProvider);

    private static FakeTimeProvider FixedClock() => new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));

    [TestMethod]
    public void TryReadFresh_FreshSnapshot_IsReturned()
    {
        var clock = FixedClock();
        var store = CreateStore(TimeSpan.FromSeconds(10));
        var timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-2);

        store.Update(new SampleSnapshot(true, timestamp, ["a"]), timestamp);

        SampleSnapshot? fresh = store.TryReadFresh(null, clock);
        Assert.IsNotNull(fresh);
        Assert.IsTrue(fresh.Connected);
    }

    [TestMethod]
    public void TryReadFresh_StaleSnapshot_ReturnsNull()
    {
        var clock = FixedClock();
        var store = CreateStore(TimeSpan.FromSeconds(10));
        var timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-30);

        store.Update(new SampleSnapshot(true, timestamp, []), timestamp);

        Assert.IsNull(store.TryReadFresh(null, clock));
    }

    [TestMethod]
    public void TryReadFresh_DefaultMaxAge_AppliesWhenNoMaxAgePassed()
    {
        var clock = FixedClock();
        var store = CreateStore(TimeSpan.FromSeconds(10));
        var freshTimestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-9);

        store.Update(new SampleSnapshot(true, freshTimestamp, []), freshTimestamp);
        Assert.IsNotNull(store.TryReadFresh(null, clock));

        var staleTimestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-11);
        store.Update(new SampleSnapshot(true, staleTimestamp, []), staleTimestamp);
        Assert.IsNull(store.TryReadFresh(null, clock));
    }

    [TestMethod]
    public void Update_NullRecord_ThrowsArgumentNullException()
    {
        var clock = FixedClock();
        var store = CreateStore(TimeSpan.FromSeconds(10));

        Assert.ThrowsExactly<ArgumentNullException>(() => store.Update(null!, clock.GetUtcNow().UtcDateTime),
            "A null record must be rejected at the store boundary");
        Assert.IsNull(store.TryReadFresh(null, clock));
        Assert.IsNotNull(store.Current, "Current never returns null — it exposes the domain's empty value");
    }

    [TestMethod]
    public void Update_DefaultProducerTimestamp_StampsReceiveTime()
    {
        var clock = FixedClock();
        var store = CreateStore(TimeSpan.FromSeconds(10), clock);

        store.Update(new SampleSnapshot(true, default, []), default);

        Assert.IsNotNull(store.TryReadFresh(null, clock),
            "A default producer timestamp is resolved to the store's receive time");
    }

    [TestMethod]
    public void Reset_RestoresEmptyValue_AndIsNotFresh()
    {
        var clock = FixedClock();
        var store = CreateStore(TimeSpan.FromSeconds(10));
        var timestamp = clock.GetUtcNow().UtcDateTime;
        store.Update(new SampleSnapshot(true, timestamp, []), timestamp);

        store.Reset();

        Assert.AreEqual(SampleSnapshot.Empty(), store.Current);
        Assert.IsNull(store.TryReadFresh(null, clock));
    }

    [TestMethod]
    public void Update_FreshnessUsesProducerTimestamp_NotReceiveTime()
    {
        var clock = FixedClock();
        var store = CreateStore(TimeSpan.FromSeconds(10));
        var oldProducerTimestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-20);

        store.Update(new SampleSnapshot(true, oldProducerTimestamp, []), oldProducerTimestamp);

        Assert.IsNull(store.TryReadFresh(null, clock),
            "Staleness is measured against the producer timestamp, not the receive time");
    }

    [TestMethod]
    public void Update_ProducerTimestamp_Preserved_ForFreshnessDecision()
    {
        var clock = FixedClock();
        var store = CreateStore(TimeSpan.FromSeconds(10));
        var producerTime = clock.GetUtcNow().UtcDateTime.AddSeconds(-2);

        store.Update(new SampleSnapshot(true, producerTime, []), producerTime);

        Assert.IsNotNull(store.TryReadFresh(null, clock));
    }

    [TestMethod]
    public void TryReadFresh_CtorTimeProvider_UsedWhenNoPerCallClockGiven()
    {
        var clock = FixedClock();
        var store = CreateStore(TimeSpan.FromSeconds(10), clock);
        var timestamp = clock.GetUtcNow().UtcDateTime.AddSeconds(-2);

        store.Update(new SampleSnapshot(true, timestamp, []), timestamp);

        Assert.IsNotNull(store.TryReadFresh());
    }

    [TestMethod]
    public void Current_ReturnsSnapshotUnderGate()
    {
        var store = CreateStore(TimeSpan.FromSeconds(10));
        var snapshot = new SampleSnapshot(true, DateTime.UtcNow, ["a", "b"]);

        store.Update(snapshot, snapshot.LastUpdate);

        Assert.AreEqual(snapshot, store.Current);
    }
}
