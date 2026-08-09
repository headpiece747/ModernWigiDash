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
}
