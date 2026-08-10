using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class TelemetryStoreMappingTests
{
    [TestInitialize]
    public void ResetStores()
    {
        // The facade tests install their own stores; a fresh store also resets
        // cache state, so the singleton is rebuilt with the system clock.
        LhmSensorStore.StoreForTest = LhmSensorStore.CreateStoreForTest(TimeProvider.System);
        FrameTimeStore.StoreForTest = FrameTimeStore.CreateStoreForTest(TimeProvider.System);
    }

    private static FakeTimeProvider FixedClock() => new(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));

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
                    Max = 90
                }
            ]
        });

        SensorSnapshotDto snap = LhmSensorStore.ReadSnapshot();

        Assert.IsTrue(snap.IsConnected);
        Assert.AreEqual(1, snap.Readings.Count);
        Assert.AreEqual("cpu-temp", snap.Readings[0].SensorId);
        Assert.AreEqual("Mainboard: CPU Package", snap.Readings[0].Label);
        Assert.AreEqual(55.5, snap.Readings[0].Value);
        Assert.IsNotNull(LhmSensorStore.TryReadFresh());
    }

    [TestMethod]
    public void LhmSensorStore_UpdateFromDto_NullDto_ProducesDisconnectedSnapshot()
    {
        LhmSensorStore.UpdateFromDto(null);

        SensorSnapshotDto snap = LhmSensorStore.ReadSnapshot();

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
            GpuBusyPercent = 71.0,
            CpuFrameTimeMs = 3.1,
            DisplayedFps = 144.0,
            DroppedFrames = 2,
            GpuTimeMs = 5.1,
            PresentModeId = 4,
            RecentFrameTimesMs = [6.9, 7.0, 6.8]
        });

        FrameTimeSnapshotDto rec = FrameTimeStore.TryReadFresh()!;

        Assert.IsTrue(rec.IsAvailable);
        Assert.AreEqual(1234, rec.ProcessId);
        Assert.AreEqual("game.exe", rec.ProcessName);
        Assert.AreEqual(144.5, rec.Fps);
        Assert.AreEqual(6.92, rec.FrameTimeMs);
        Assert.AreEqual(90.1, rec.Low1PercentFps);
        Assert.AreEqual(60.2, rec.Low01PercentFps);
        Assert.AreEqual(71.0, rec.GpuBusyPercent);
        Assert.AreEqual(3.1, rec.CpuFrameTimeMs);
        Assert.AreEqual(144.0, rec.DisplayedFps);
        Assert.AreEqual(2, rec.DroppedFrames);
        Assert.AreEqual(5.1, rec.GpuTimeMs);
        Assert.AreEqual(4, rec.PresentModeId);
        CollectionAssert.AreEqual(new[] { 6.9, 7.0, 6.8 }, rec.RecentFrameTimesMs.ToArray());
        Assert.IsNotNull(FrameTimeStore.TryReadFresh());
    }

    [TestMethod]
    public void FrameTimeStore_UpdateFromDto_NullDto_ProducesUnavailableRecord()
    {
        FrameTimeStore.UpdateFromDto(null);

        FrameTimeSnapshotDto rec = FrameTimeStore.TryReadFresh()!;

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
        var clock = FixedClock();
        FrameTimeStore.StoreForTest = FrameTimeStore.CreateStoreForTest(clock);
        var producerTime = clock.GetUtcNow().UtcDateTime;
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto { IsAvailable = true, LastUpdate = producerTime });

        Assert.AreEqual(producerTime, FrameTimeStore.TryReadFresh()!.LastUpdate);
    }

    [TestMethod]
    public void LhmSensorStore_UpdateFromDto_WithoutProducerTimestamp_StoreStampsReceiveTime()
    {
        LhmSensorStore.UpdateFromDto(new SensorSnapshotDto { IsConnected = true, Readings = [] });

        Assert.IsNotNull(LhmSensorStore.TryReadFresh(),
            "A DTO without a producer timestamp must still read as fresh — the store stamps the receive time");
    }

    // ── TryReadFresh: store-owned staleness ─────────────────

    [TestMethod]
    public void LhmSensorStore_TryReadFresh_FreshSnapshot_IsReturned()
    {
        var clock = FixedClock();
        LhmSensorStore.StoreForTest = LhmSensorStore.CreateStoreForTest(clock);
        LhmSensorStore.UpdateFromDto(new SensorSnapshotDto
        {
            IsConnected = true,
            LastUpdate = clock.GetUtcNow().UtcDateTime.AddSeconds(-2),
            Readings = []
        });

        SensorSnapshotDto? fresh = LhmSensorStore.TryReadFresh();

        Assert.IsNotNull(fresh);
        Assert.IsTrue(fresh.IsConnected);
    }

    [TestMethod]
    public void LhmSensorStore_TryReadFresh_StaleSnapshot_ReturnsNull()
    {
        var clock = FixedClock();
        LhmSensorStore.StoreForTest = LhmSensorStore.CreateStoreForTest(clock);
        LhmSensorStore.UpdateFromDto(new SensorSnapshotDto
        {
            IsConnected = true,
            LastUpdate = clock.GetUtcNow().UtcDateTime.AddSeconds(-30),
            Readings = []
        });

        Assert.IsNull(LhmSensorStore.TryReadFresh());
    }

    [TestMethod]
    public void LhmSensorStore_TryReadFresh_DisconnectedButFresh_IsReturnedForWidgetToDecide()
    {
        var clock = FixedClock();
        LhmSensorStore.StoreForTest = LhmSensorStore.CreateStoreForTest(clock);
        LhmSensorStore.UpdateFromDto(null);

        SensorSnapshotDto? fresh = LhmSensorStore.TryReadFresh();

        Assert.IsNotNull(fresh, "A null-DTO snapshot is stamped with the receive time — freshness ≠ connectivity");
        Assert.IsFalse(fresh.IsConnected, "The widget renders the unavailable state via IsConnected");
    }

    [TestMethod]
    public void FrameTimeStore_TryReadFresh_StaleSnapshot_ReturnsNull()
    {
        var clock = FixedClock();
        FrameTimeStore.StoreForTest = FrameTimeStore.CreateStoreForTest(clock);
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            LastUpdate = clock.GetUtcNow().UtcDateTime.AddSeconds(-30)
        });

        Assert.IsNull(FrameTimeStore.TryReadFresh());
    }

    [TestMethod]
    public void FrameTimeStore_TryReadFresh_UnavailableButFresh_IsReturned()
    {
        var clock = FixedClock();
        FrameTimeStore.StoreForTest = FrameTimeStore.CreateStoreForTest(clock);
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = false,
            LastUpdate = clock.GetUtcNow().UtcDateTime
        });

        FrameTimeSnapshotDto? fresh = FrameTimeStore.TryReadFresh();

        Assert.IsNotNull(fresh);
        Assert.IsFalse(fresh.IsAvailable, "A fresh but unavailable record is not stale — the widget decides presentation");
    }

    [TestMethod]
    public void FrameTimeStore_UpdateAndRead_RoundTrips()
    {
        var record = new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            ProcessId = 1234,
            ProcessName = "game.exe",
            Fps = 144.0,
            FrameTimeMs = 6.94,
            Low1PercentFps = 112.0,
            Low01PercentFps = 89.0,
            GpuBusyPercent = 92.0,
            CpuFrameTimeMs = 4.1,
            DisplayedFps = 144.0,
            DroppedFrames = 1,
            GpuTimeMs = 6.0,
            PresentModeId = 4,
            RecentFrameTimesMs = [6.9, 7.0, 7.1, 6.8],
        };

        FrameTimeStore.Update(record);
        FrameTimeSnapshotDto read = FrameTimeStore.TryReadFresh()!;

        Assert.IsTrue(read.IsAvailable);
        Assert.AreEqual("game.exe", read.ProcessName);
        Assert.AreEqual(144.0, read.Fps);
        Assert.AreEqual(92.0, read.GpuBusyPercent);
        Assert.AreEqual(144.0, read.DisplayedFps);
        Assert.AreEqual(1, read.DroppedFrames);
        Assert.AreEqual(6.0, read.GpuTimeMs);
        Assert.AreEqual(4, read.PresentModeId);
        CollectionAssert.AreEqual(new[] { 6.9, 7.0, 7.1, 6.8 }, read.RecentFrameTimesMs.ToArray());
    }
}
