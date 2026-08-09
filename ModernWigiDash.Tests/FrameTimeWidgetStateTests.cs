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
    private static void RenderWith(DateTime now, bool isAvailable, bool captureHealthy, int processId, double fps)
    {
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = isAvailable,
            CaptureHealthy = captureHealthy,
            ProcessId = processId,
            Fps = fps,
            LastUpdate = now,
        });

        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var widget = new FrameTimeWidget();
        widget.Render(surface.Canvas, new SKRect(0, 0, 1016, 592));
        FrameTimeStore.Reset();
    }

    [TestMethod]
    public void Render_CaptureInactive_RendersPlaceholderWithoutThrowing()
    {
        RenderWith(DateTime.UtcNow, isAvailable: true, captureHealthy: false, processId: -1, fps: 0);
    }

    [TestMethod]
    public void Render_Unavailable_RendersPlaceholderWithoutThrowing()
    {
        RenderWith(DateTime.UtcNow, isAvailable: false, captureHealthy: true, processId: -1, fps: 0);
    }

    [TestMethod]
    public void Render_IdleMonitorMode_RendersWithoutThrowing()
    {
        RenderWith(DateTime.UtcNow, isAvailable: true, captureHealthy: true, processId: -1, fps: 0);
    }

    [TestMethod]
    public void Render_LiveFps_RendersWithoutThrowing()
    {
        RenderWith(DateTime.UtcNow, isAvailable: true, captureHealthy: true, processId: 4321, fps: 143.2);
    }
}
