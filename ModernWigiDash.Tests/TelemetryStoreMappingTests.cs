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
}
