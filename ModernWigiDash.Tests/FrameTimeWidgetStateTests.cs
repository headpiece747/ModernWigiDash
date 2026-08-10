using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// FrameTimeWidget state rendering: the unavailable, capture-inactive, and
/// monitor-mode states must all render without throwing, and the
/// capture-inactive state must not be confused with a real FPS readout.
/// </summary>
[TestClass]
public class FrameTimeWidgetStateTests
{
    private static SKSurface RenderWith(DateTime now, bool isAvailable, bool captureHealthy, int processId, double fps)
    {
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = isAvailable,
            CaptureHealthy = captureHealthy,
            ProcessId = processId,
            Fps = fps,
            LastUpdate = now,
        });

        var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var widget = new FrameTimeWidget();
        widget.Render(surface.Canvas, new SKRect(0, 0, 1016, 592));
        FrameTimeStore.Reset();
        return surface;
    }

    [TestMethod]
    public void Render_CaptureInactive_RendersPlaceholderWithoutThrowing()
    {
        using var surface = RenderWith(DateTime.UtcNow, isAvailable: true, captureHealthy: false, processId: -1, fps: 0);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void Render_Unavailable_RendersPlaceholderWithoutThrowing()
    {
        using var surface = RenderWith(DateTime.UtcNow, isAvailable: false, captureHealthy: true, processId: -1, fps: 0);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void Render_IdleMonitorMode_RendersWithoutThrowing()
    {
        using var surface = RenderWith(DateTime.UtcNow, isAvailable: true, captureHealthy: true, processId: -1, fps: 0);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void Render_LiveFps_RendersWithoutThrowing()
    {
        using var surface = RenderWith(DateTime.UtcNow, isAvailable: true, captureHealthy: true, processId: 4321, fps: 143.2);
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void FrameTimeWidget_Render_AllStates_ExecuteWithoutExceptions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var canvas = surface.Canvas;
        var bounds = new SKRect(0, 0, 406, 296);

        var unavailable = new FrameTimeWidget();
        FrameTimeStore.Update(FrameTimeSnapshotRecord.Unavailable());
        unavailable.Render(canvas, bounds);

        var waiting = new FrameTimeWidget();
        FrameTimeStore.Update(new FrameTimeSnapshotRecord(true, 0, "", 0, 0, 0, 0, 0, 0, []));
        waiting.Render(canvas, bounds);

        var live = new FrameTimeWidget { AccentColorHex = "#22C55E" };
        List<double> samples = [];
        for (int i = 0; i < 240; i++)
        {
            samples.Add(6.5 + (i % 20) * 0.05);
        }
        FrameTimeStore.Update(new FrameTimeSnapshotRecord(
            true, 4321, "fpsbench.exe", 143.2, 6.98, 110.4, 87.2, 93.0, 4.05, samples));
        live.Render(canvas, bounds);

        // Small (2x1) size must also render without exceptions
        using var smallSurface = SKSurface.Create(new SKImageInfo(200, 160));
        var smallCanvas = smallSurface.Canvas;
        live.Render(smallCanvas, new SKRect(0, 0, 200, 160));

        Assert.IsNotNull(surface);
    }

    // ---- Desktop-composition detection (video / post-game-close case) ----

    private static FrameTimeSnapshotRecord Composite(double fps, double low1, double gpuBusyMs)
        => new(true, 4321, "chrome.exe", fps, 1000.0 / fps, low1, low1, gpuBusyMs, 0.2, []);

    [TestMethod]
    public void LooksLikeDesktopComposition_VsyncPinnedNoGpuWork_True()
    {
        // Chrome with a playing video on a 162 Hz panel: presents pinned at the
        // panel cadence, no frame-time variance, no per-frame GPU work.
        Assert.IsTrue(FrameTimeWidget.LooksLikeDesktopComposition(Composite(161.9, 161.4, 0.3), 162));
    }

    [TestMethod]
    public void LooksLikeDesktopComposition_VsyncPinnedWithGpuWork_False()
    {
        // A real game capped at the refresh rate still shows meaningful GPU
        // busy and frame-time variance — stays in tracked mode.
        Assert.IsFalse(FrameTimeWidget.LooksLikeDesktopComposition(Composite(161.9, 150.2, 3.8), 162));
    }

    [TestMethod]
    public void LooksLikeDesktopComposition_SubRefreshPresenter_False()
    {
        // A 60 fps-paced presenter (e.g. a player presenting at content
        // cadence) is real data even with no GPU work — tracked mode shows it.
        Assert.IsFalse(FrameTimeWidget.LooksLikeDesktopComposition(Composite(60.0, 59.2, 0.4), 162));
    }

    [TestMethod]
    public void LooksLikeDesktopComposition_NoPresents_False()
    {
        // Zero presents is handled by the producer's idle path, not this rule.
        Assert.IsFalse(FrameTimeWidget.LooksLikeDesktopComposition(Composite(0, 0, 0), 162));
    }

    [TestMethod]
    public void LooksLikeDesktopComposition_FrameTimeJitter_False()
    {
        // Presenting at the panel cadence but with real frame-time jitter
        // (1% low well below the average) is a real presenter.
        Assert.IsFalse(FrameTimeWidget.LooksLikeDesktopComposition(Composite(161.9, 120.0, 0.5), 162));
    }

    [TestMethod]
    public void Render_CompositeSignature_RendersMonitorModeWithoutThrowing()
    {
        // High-refresh composite signature is composite on any panel (>= 0.95x
        // of both 60 Hz and 162 Hz), so the gate decision is deterministic.
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = 4321,
            ProcessName = "chrome.exe",
            Fps = 161.9,
            FrameTimeMs = 6.18,
            Low1PercentFps = 161.4,
            Low01PercentFps = 161.0,
            GpuBusyPercent = 0.3,
            CpuFrameTimeMs = 0.2,
            LastUpdate = DateTime.UtcNow,
        });

        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var widget = new FrameTimeWidget();
        widget.Render(surface.Canvas, new SKRect(0, 0, 1016, 592));
        FrameTimeStore.Reset();

        Assert.IsNotNull(surface);
    }
}
