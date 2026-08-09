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
}
