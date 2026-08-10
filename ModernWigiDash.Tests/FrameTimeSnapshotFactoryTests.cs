using ModernWigiDash.App.PresentMon;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// The snapshot-outcome shaping: unit conversions and percentile derivation
/// that used to live inside the producer's 100-line poll method.
/// </summary>
[TestClass]
public class FrameTimeSnapshotFactoryTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);

    private static PresentMonDynamicSample Sample(double fps = 143.2, double busyMs = 4.0)
        => new(fps, 110.4, busyMs, 4.05, 142.8, 2, 6.1, 4);

    [TestMethod]
    public void Unavailable_CarriesReasonAndFlag()
    {
        var dto = FrameTimeSnapshotFactory.Unavailable("PresentMonAPI2.dll not found", Now);

        Assert.IsFalse(dto.IsAvailable);
        Assert.AreEqual("PresentMonAPI2.dll not found", dto.ErrorMessage);
        Assert.AreEqual(Now, dto.LastUpdate);
    }

    [TestMethod]
    public void Idle_NoProcess_Healthy()
    {
        var dto = FrameTimeSnapshotFactory.Idle(Now);

        Assert.IsTrue(dto.IsAvailable);
        Assert.IsTrue(dto.CaptureHealthy);
        Assert.AreEqual(-1, dto.ProcessId);
        Assert.AreEqual(0, dto.Fps);
    }

    [TestMethod]
    public void CaptureDead_FlagsUnhealthyWithMessage()
    {
        var dto = FrameTimeSnapshotFactory.CaptureDead(Now);

        Assert.IsTrue(dto.IsAvailable, "the service is reachable — availability stays true");
        Assert.IsFalse(dto.CaptureHealthy);
        StringAssert.Contains(dto.ErrorMessage, "not producing present data");
        Assert.AreEqual(-1, dto.ProcessId);
    }

    [TestMethod]
    public void Live_ComputesFrameTimeAndBusyPercent()
    {
        // 143.2 fps → 6.98 ms; 4.0 ms busy per frame → 57.28 % of frame time.
        var dto = FrameTimeSnapshotFactory.Live(4321, "game.exe", Sample(), [6.5, 6.7], Now);

        Assert.AreEqual(1000.0 / 143.2, dto.FrameTimeMs, 0.001);
        Assert.AreEqual(4.0 * 143.2 / 10.0, dto.GpuBusyPercent, 0.001);
        Assert.AreEqual(143.2, dto.Fps, 0.001);
        Assert.AreEqual(110.4, dto.Low1PercentFps, 0.001);
        Assert.AreEqual(142.8, dto.DisplayedFps, 0.001);
        Assert.AreEqual(2, dto.DroppedFrames);
        Assert.AreEqual(6.1, dto.GpuTimeMs, 0.001);
        Assert.AreEqual(4, dto.PresentModeId);
        Assert.AreEqual("game.exe", dto.ProcessName);
    }

    [TestMethod]
    public void Live_ComputesLow01FromBufferedFrameTimes()
    {
        List<double> frameTimes = [6.5, 6.7];

        var dto = FrameTimeSnapshotFactory.Live(4321, "game.exe", Sample(), frameTimes, Now);

        Assert.AreEqual(FrameTimeStatistics.Low01PercentFps(frameTimes), dto.Low01PercentFps, 0.001);
        CollectionAssert.AreEqual(frameTimes.ToArray(), dto.RecentFrameTimesMs.ToArray(),
            "the snapshot must carry a copy of the buffer, not a reference");
    }

    [TestMethod]
    public void Live_ZeroFps_ZeroFrameTimeAndBusyPercent()
    {
        var dto = FrameTimeSnapshotFactory.Live(4321, "game.exe", Sample(fps: 0, busyMs: 0), [], Now);

        Assert.AreEqual(0, dto.FrameTimeMs);
        Assert.AreEqual(0, dto.GpuBusyPercent, "no frames, no busy-per-frame percentage");
    }
}
