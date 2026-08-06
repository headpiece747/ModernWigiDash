using System.Runtime.Serialization;
using ModernWigiDash.Service.Contracts;

namespace ModernWigiDash.Tests;

[TestClass]
public class ServiceContractTests
{
    [TestMethod]
    public void FrameTimeSnapshotDto_Defaults_AreSafeEmpty()
    {
        var dto = new FrameTimeSnapshotDto();

        Assert.IsFalse(dto.IsAvailable);
        Assert.AreEqual(string.Empty, dto.ErrorMessage);
        Assert.AreEqual(0, dto.ProcessId);
        Assert.AreEqual(string.Empty, dto.ProcessName);
        Assert.AreEqual(0.0, dto.Fps);
        Assert.AreEqual(0.0, dto.FrameTimeMs);
        Assert.AreEqual(0.0, dto.Low1PercentFps);
        Assert.AreEqual(0.0, dto.Low01PercentFps);
        Assert.AreEqual(0.0, dto.GpuBusyPercent);
        Assert.AreEqual(0.0, dto.CpuFrameTimeMs);
        Assert.IsNotNull(dto.RecentFrameTimesMs);
        Assert.AreEqual(0, dto.RecentFrameTimesMs.Count);
    }

    [TestMethod]
    public void FrameTimeSnapshotDto_DataContract_RoundTripsAllMembers()
    {
        var dto = new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            ErrorMessage = "",
            LastUpdate = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ProcessId = 4242,
            ProcessName = "game.exe",
            Fps = 144.5,
            FrameTimeMs = 6.92,
            Low1PercentFps = 90.1,
            Low01PercentFps = 60.2,
            GpuBusyPercent = 45.3,
            CpuFrameTimeMs = 3.1,
            RecentFrameTimesMs = [6.9, 7.0, 6.8]
        };

        FrameTimeSnapshotDto clone = RoundTrip(dto);

        Assert.IsTrue(clone.IsAvailable);
        Assert.AreEqual(dto.LastUpdate, clone.LastUpdate);
        Assert.AreEqual(dto.ProcessId, clone.ProcessId);
        Assert.AreEqual(dto.ProcessName, clone.ProcessName);
        Assert.AreEqual(dto.Fps, clone.Fps);
        Assert.AreEqual(dto.FrameTimeMs, clone.FrameTimeMs);
        Assert.AreEqual(dto.Low1PercentFps, clone.Low1PercentFps);
        Assert.AreEqual(dto.Low01PercentFps, clone.Low01PercentFps);
        Assert.AreEqual(dto.GpuBusyPercent, clone.GpuBusyPercent);
        Assert.AreEqual(dto.CpuFrameTimeMs, clone.CpuFrameTimeMs);
        CollectionAssert.AreEqual(dto.RecentFrameTimesMs, clone.RecentFrameTimesMs);
    }

    [TestMethod]
    public void SensorSnapshotDto_Defaults_HaveEmptyReadings()
    {
        var dto = new SensorSnapshotDto();

        Assert.IsFalse(dto.IsConnected);
        Assert.AreEqual(default, dto.LastUpdate);
        Assert.IsNotNull(dto.Readings);
        Assert.AreEqual(0, dto.Readings.Count);
    }

    [TestMethod]
    public void SensorReadingDto_Defaults_AreSafeEmpty()
    {
        var dto = new SensorReadingDto();

        Assert.AreEqual(string.Empty, dto.SensorId);
        Assert.AreEqual(string.Empty, dto.SensorName);
        Assert.AreEqual(string.Empty, dto.HardwareName);
        Assert.AreEqual(string.Empty, dto.HardwareType);
        Assert.AreEqual(string.Empty, dto.SensorType);
        Assert.AreEqual(string.Empty, dto.Unit);
        Assert.AreEqual(0.0, dto.Value);
        Assert.AreEqual(0.0, dto.Min);
        Assert.AreEqual(0.0, dto.Max);
        Assert.AreEqual(0.0, dto.Avg);
    }

    [TestMethod]
    public void SensorSnapshotDto_DataContract_RoundTripsNestedReadings()
    {
        var dto = new SensorSnapshotDto
        {
            IsConnected = true,
            LastUpdate = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            Readings =
            [
                new SensorReadingDto { SensorId = "cpu-temp", SensorName = "CPU Package", Value = 61.5, Min = 40.0, Max = 72.0, Avg = 55.0 },
                new SensorReadingDto { SensorId = "gpu-load", SensorName = "GPU Core", Value = 84.0, Min = 2.0, Max = 100.0, Avg = 50.0 }
            ]
        };

        SensorSnapshotDto clone = RoundTrip(dto);

        Assert.IsTrue(clone.IsConnected);
        Assert.AreEqual(dto.LastUpdate, clone.LastUpdate);
        Assert.AreEqual(2, clone.Readings.Count);
        Assert.AreEqual("cpu-temp", clone.Readings[0].SensorId);
        Assert.AreEqual(61.5, clone.Readings[0].Value);
        Assert.AreEqual("GPU Core", clone.Readings[1].SensorName);
        Assert.AreEqual(84.0, clone.Readings[1].Value);
    }

    private static T RoundTrip<T>(T value)
    {
        var serializer = new DataContractSerializer(typeof(T));
        using var stream = new MemoryStream();
        serializer.WriteObject(stream, value);
        stream.Position = 0;
        return (T)serializer.ReadObject(stream)!;
    }
}
