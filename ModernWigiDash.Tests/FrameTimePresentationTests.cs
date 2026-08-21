namespace ModernWigiDash.Tests;

/// <summary>
/// The widget's display rules: exact strings (hero, eight dashboard cards,
/// nine overlay rows), the zero-value no-process semantics, and the
/// placement-size visibility thresholds — assertable without pixels.
/// </summary>
[TestClass]
public class FrameTimePresentationTests
{
    private static FrameTimeSnapshotDto LiveSnapshot() => new()
    {
        IsAvailable = true,
        CaptureHealthy = true,
        ProcessId = 4321,
        ProcessName = "game.exe",
        Fps = 162.4,
        FrameTimeMs = 6.16,
        Low1PercentFps = 138.0,
        Low01PercentFps = 121.0,
        GpuBusyPercent = 71.0,
        CpuFrameTimeMs = 5.2,
        DisplayedFps = 162.0,
        DroppedFrames = 3,
        GpuTimeMs = 6.1,
        PresentModeId = 8,
        RecentFrameTimesMs = [6.5, 6.7, 6.4],
    };

    private static FrameTimeSnapshotDto NoProcessSnapshot() => new()
    {
        IsAvailable = true,
        CaptureHealthy = true,
        ProcessId = -1,
    };

    private static FrameTimeDisplay Build(FrameTimeSnapshotDto snapshot, float width = 1016f, float height = 592f)
        => FrameTimePresentation.Build(snapshot, new SKSize(width, height));

    [TestMethod]
    public void Build_LiveSnapshot_FormatsHeroAndAllEightDashboardCards()
    {
        var d = Build(LiveSnapshot());

        Assert.AreEqual("162", d.HeroFps);
        Assert.AreEqual("6.2 ms", d.HeroFrameTimeMs);
        Assert.AreEqual(8, d.Dashboard.Count);
        Assert.AreEqual(("1% LOW", "138 FPS"), (d.Dashboard[0].Label, d.Dashboard[0].Value));
        Assert.AreEqual(("0.1% LOW", "121 FPS"), (d.Dashboard[1].Label, d.Dashboard[1].Value));
        Assert.AreEqual(("CPU FRAME", "5.2 ms"), (d.Dashboard[2].Label, d.Dashboard[2].Value));
        Assert.AreEqual(("GPU BUSY", "71%"), (d.Dashboard[3].Label, d.Dashboard[3].Value));
        Assert.AreEqual(("DISPLAYED", "162 FPS"), (d.Dashboard[4].Label, d.Dashboard[4].Value));
        Assert.AreEqual(("DROPPED", "3"), (d.Dashboard[5].Label, d.Dashboard[5].Value));
        Assert.AreEqual(("GPU TIME", "6.1 ms"), (d.Dashboard[6].Label, d.Dashboard[6].Value));
        Assert.AreEqual(("PRESENT MODE", "HWC Ind. Flip"), (d.Dashboard[7].Label, d.Dashboard[7].Value));
    }

    [TestMethod]
    public void Build_NoProcess_AllValuesReadZero()
    {
        var d = Build(NoProcessSnapshot());

        Assert.AreEqual("0", d.HeroFps);
        Assert.AreEqual("0.0 ms", d.HeroFrameTimeMs);
        Assert.AreEqual(("1% LOW", "0 FPS"), (d.Dashboard[0].Label, d.Dashboard[0].Value));
        Assert.AreEqual(("GPU BUSY", "0%"), (d.Dashboard[3].Label, d.Dashboard[3].Value));
        Assert.AreEqual(("PRESENT MODE", "—"), (d.Dashboard[7].Label, d.Dashboard[7].Value),
            "the present mode has no numeric zero — it renders the no-data dash");
        Assert.AreEqual(("Presented FPS", "0"), (d.Overlay[0].Label, d.Overlay[0].Value));
        Assert.AreEqual(("99th %tile Frame Time", "0.0 ms"), (d.Overlay[2].Label, d.Overlay[2].Value));
        Assert.AreEqual(("Present Mode", "—"), (d.Overlay[8].Label, d.Overlay[8].Value));
        Assert.IsFalse(d.ShowProcessName);
        Assert.IsFalse(d.ShowGraph, "no samples, no sparkline");
    }

    [TestMethod]
    public void Build_TrackedButIdle_ReadsSameZerosAsNoProcess()
    {
        var d = Build(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = 4321,
            ProcessName = "game.exe",
            Fps = 0,
        });

        Assert.AreEqual("0", d.HeroFps);
        Assert.AreEqual("0.0 ms", d.HeroFrameTimeMs);
        Assert.AreEqual(("1% LOW", "0 FPS"), (d.Dashboard[0].Label, d.Dashboard[0].Value));
        Assert.IsTrue(d.ShowProcessName, "a tracked process keeps its name even at 0 fps");
    }

    [TestMethod]
    public void Build_OverlayRows_ListPresentMonMetricNamesWithDerivedFrameTimes()
    {
        var d = Build(LiveSnapshot());

        Assert.AreEqual(9, d.Overlay.Count);
        Assert.AreEqual(("Presented FPS", "162"), (d.Overlay[0].Label, d.Overlay[0].Value));
        Assert.AreEqual(("99th %tile Frame Time", "7.2 ms"), (d.Overlay[2].Label, d.Overlay[2].Value), "1000 / 138 fps");
        Assert.AreEqual(("1st %tile Frame Time", "8.3 ms"), (d.Overlay[3].Label, d.Overlay[3].Value), "1000 / 121 fps");
        Assert.AreEqual(("GPU Busy %", "71%"), (d.Overlay[4].Label, d.Overlay[4].Value));
        Assert.AreEqual(("Present Mode", "Hardware Composed: Independent Flip"), (d.Overlay[8].Label, d.Overlay[8].Value));
    }

    [TestMethod]
    public void Build_OverlayLineCount_ClipsByHeight()
    {
        Assert.AreEqual(1, Build(LiveSnapshot(), height: 100f).OverlayLineCount);
        Assert.AreEqual(4, Build(LiveSnapshot(), height: 120f).OverlayLineCount);
        Assert.AreEqual(9, Build(LiveSnapshot(), height: 200f).OverlayLineCount);
    }

    [TestMethod]
    public void Build_VisibilityFlags_FollowPlacementSizeThresholds()
    {
        var d = Build(LiveSnapshot(), width: 300f, height: 100f);
        Assert.IsFalse(d.ShowMetricCards);
        Assert.IsFalse(d.ShowSecondRow);
        Assert.IsFalse(d.ShowGraph);

        d = Build(LiveSnapshot(), width: 450f, height: 100f);
        Assert.IsTrue(d.ShowMetricCards);
        Assert.IsFalse(d.ShowSecondRow);

        d = Build(LiveSnapshot(), width: 600f, height: 100f);
        Assert.IsTrue(d.ShowSecondRow);

        d = Build(LiveSnapshot(), width: 600f, height: 200f);
        Assert.IsTrue(d.ShowGraph);
    }
}
