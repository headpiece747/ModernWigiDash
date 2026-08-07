using ModernWigiDash.Service.Contracts;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class TelemetryStoreMappingTests
{
    [TestInitialize]
    public void ResetStores()
    {
        LhmSensorStore.Reset();
        FrameTimeStore.Reset();
    }

    [TestMethod]
    public void LhmSensorStore_UpdateFromDto_MapsReadingsAndTracksFreshness()
    {
        LhmSensorStore.UpdateFromDto(new SensorSnapshotDto
        {
            IsConnected = true,
            Readings =
            [
                new SensorReadingDto
                {
                    SensorId = "cpu-temp",
                    SensorName = "CPU Package",
                    HardwareName = "Mainboard",
                    Unit = "°C",
                    Value = 55.5,
                    Min = 40,
                    Max = 90,
                    Avg = 52
                }
            ]
        });

        LhmSnapshot snap = LhmSensorStore.ReadSnapshot();

        Assert.IsTrue(snap.IsConnected);
        Assert.AreEqual(1, snap.Readings.Count);
        Assert.AreEqual("cpu-temp", snap.Readings[0].SensorId);
        Assert.AreEqual("Mainboard: CPU Package", snap.Readings[0].Label);
        Assert.AreEqual(55.5, snap.Readings[0].Value);
        Assert.IsTrue(snap.IsFresh(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public void LhmSensorStore_UpdateFromDto_NullDto_ProducesDisconnectedSnapshot()
    {
        LhmSensorStore.UpdateFromDto(null);

        LhmSnapshot snap = LhmSensorStore.ReadSnapshot();

        Assert.IsFalse(snap.IsConnected);
        Assert.AreEqual(0, snap.Readings.Count);
    }

    [TestMethod]
    public void FrameTimeStore_UpdateFromDto_MapsAllMetrics()
    {
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            ProcessId = 1234,
            ProcessName = "game.exe",
            Fps = 144.5,
            FrameTimeMs = 6.92,
            Low1PercentFps = 90.1,
            Low01PercentFps = 60.2,
            GpuBusyPercent = 45.3,
            CpuFrameTimeMs = 3.1,
            RecentFrameTimesMs = [6.9, 7.0, 6.8]
        });

        FrameTimeSnapshotRecord rec = FrameTimeStore.ReadSnapshot();

        Assert.IsTrue(rec.IsAvailable);
        Assert.AreEqual(1234, rec.ProcessId);
        Assert.AreEqual("game.exe", rec.ProcessName);
        Assert.AreEqual(144.5, rec.Fps);
        Assert.AreEqual(6.92, rec.FrameTimeMs);
        Assert.AreEqual(90.1, rec.Low1PercentFps);
        Assert.AreEqual(60.2, rec.Low01PercentFps);
        Assert.AreEqual(45.3, rec.GpuBusyPercent);
        Assert.AreEqual(3.1, rec.CpuFrameTimeMs);
        CollectionAssert.AreEqual(new[] { 6.9, 7.0, 6.8 }, rec.RecentFrameTimesMs.ToArray());
        Assert.IsTrue(rec.IsFresh(TimeSpan.FromSeconds(10)));
    }

    [TestMethod]
    public void FrameTimeStore_UpdateFromDto_NullDto_ProducesUnavailableRecord()
    {
        FrameTimeStore.UpdateFromDto(null);

        FrameTimeSnapshotRecord rec = FrameTimeStore.ReadSnapshot();

        Assert.IsFalse(rec.IsAvailable);
    }

    // ── producer timestamp preservation ─────────────────────

    [TestMethod]
    public void LhmSensorStore_UpdateFromDto_PreservesProducerTimestamp()
    {
        var producerTime = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        LhmSensorStore.UpdateFromDto(new SensorSnapshotDto
        {
            IsConnected = true,
            LastUpdate = producerTime,
            Readings = []
        });

        Assert.AreEqual(producerTime, LhmSensorStore.ReadSnapshot().LastUpdate);
    }

    [TestMethod]
    public void FrameTimeStore_UpdateFromDto_PreservesProducerTimestamp()
    {
        var producerTime = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto { IsAvailable = true, LastUpdate = producerTime });

        Assert.AreEqual(producerTime, FrameTimeStore.ReadSnapshot().LastUpdate);
    }

    [TestMethod]
    public void LhmSensorStore_UpdateFromDto_WithoutProducerTimestamp_FallsBackToReceiveTime()
    {
        LhmSensorStore.UpdateFromDto(new SensorSnapshotDto { IsConnected = true, Readings = [] });

        Assert.IsTrue(LhmSensorStore.ReadSnapshot().IsFresh(TimeSpan.FromMinutes(1)));
    }

    // ── TryReadFresh: store-owned staleness ─────────────────

    [TestMethod]
    public void LhmSensorStore_TryReadFresh_FreshSnapshot_IsReturned()
    {
        var clock = new TimeProviderFake(new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc));
        LhmSensorStore.UpdateFromDto(new SensorSnapshotDto
        {
            IsConnected = true,
            LastUpdate = clock.GetUtcNow().UtcDateTime.AddSeconds(-2),
            Readings = []
        });

        LhmSnapshot? fresh = LhmSensorStore.TryReadFresh(TimeSpan.FromSeconds(10), clock);

        Assert.IsNotNull(fresh);
        Assert.IsTrue(fresh!.IsConnected);
    }

    [TestMethod]
    public void LhmSensorStore_TryReadFresh_StaleSnapshot_ReturnsNull()
    {
        var clock = new TimeProviderFake(new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc));
        LhmSensorStore.UpdateFromDto(new SensorSnapshotDto
        {
            IsConnected = true,
            LastUpdate = clock.GetUtcNow().UtcDateTime.AddSeconds(-30),
            Readings = []
        });

        Assert.IsNull(LhmSensorStore.TryReadFresh(TimeSpan.FromSeconds(10), clock));
    }

    [TestMethod]
    public void LhmSensorStore_TryReadFresh_DisconnectedButFresh_IsReturnedForWidgetToDecide()
    {
        var clock = new TimeProviderFake(new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc));
        LhmSensorStore.UpdateFromDto(null);

        LhmSnapshot? fresh = LhmSensorStore.TryReadFresh(TimeSpan.FromSeconds(10), clock);

        Assert.IsNotNull(fresh, "A null-DTO snapshot is stamped with the receive time — freshness ≠ connectivity");
        Assert.IsFalse(fresh!.IsConnected, "The widget renders the unavailable state via IsConnected");
    }

    [TestMethod]
    public void FrameTimeStore_TryReadFresh_StaleSnapshot_ReturnsNull()
    {
        var clock = new TimeProviderFake(new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc));
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            LastUpdate = clock.GetUtcNow().UtcDateTime.AddSeconds(-30)
        });

        Assert.IsNull(FrameTimeStore.TryReadFresh(TimeSpan.FromSeconds(5), clock));
    }

    [TestMethod]
    public void FrameTimeStore_TryReadFresh_UnavailableButFresh_IsReturned()
    {
        var clock = new TimeProviderFake(new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc));
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = false,
            LastUpdate = clock.GetUtcNow().UtcDateTime
        });

        FrameTimeSnapshotRecord? fresh = FrameTimeStore.TryReadFresh(TimeSpan.FromSeconds(5), clock);

        Assert.IsNotNull(fresh);
        Assert.IsFalse(fresh!.IsAvailable, "A fresh but unavailable record is not stale — the widget decides presentation");
    }

    private sealed class TimeProviderFake : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public TimeProviderFake(DateTime now) => _now = new DateTimeOffset(now);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
