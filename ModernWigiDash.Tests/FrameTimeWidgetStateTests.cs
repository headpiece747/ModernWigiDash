using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// FrameTimeWidget state rendering: the unavailable, capture-inactive, and
/// dash (no-process) states must all render without throwing, and the
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
    public void Render_NoProcess_RendersWithoutThrowing()
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
        FrameTimeStore.Update(new FrameTimeSnapshotDto());
        unavailable.Render(canvas, bounds);

        var waiting = new FrameTimeWidget();
        FrameTimeStore.Update(new FrameTimeSnapshotDto { IsAvailable = true, CaptureHealthy = true });
        waiting.Render(canvas, bounds);

        var live = new FrameTimeWidget { AccentColorHex = "#22C55E" };
        List<double> samples = [];
        for (int i = 0; i < 240; i++)
        {
            samples.Add(6.5 + (i % 20) * 0.05);
        }
        FrameTimeStore.Update(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = 4321,
            ProcessName = "fpsbench.exe",
            Fps = 143.2,
            FrameTimeMs = 6.98,
            Low1PercentFps = 110.4,
            Low01PercentFps = 87.2,
            GpuBusyPercent = 93.0,
            CpuFrameTimeMs = 4.05,
            RecentFrameTimesMs = samples,
        });
        live.Render(canvas, bounds);

        // Small (2x1) size must also render without exceptions
        using var smallSurface = SKSurface.Create(new SKImageInfo(200, 160));
        var smallCanvas = smallSurface.Canvas;
        live.Render(smallCanvas, new SKRect(0, 0, 200, 160));

        Assert.IsNotNull(surface);
    }

    private static void RenderWith(FrameTimeSnapshotDto dto, out FrameTimeWidget widget)
    {
        FrameTimeStore.UpdateFromDto(dto);
        var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        widget = new FrameTimeWidget();
        widget.Render(surface.Canvas, new SKRect(0, 0, 1016, 592));
        surface.Dispose();
    }

    [TestMethod]
    public void Render_NoProcessTracked_RendersDashLayoutWithoutThrowing()
    {
        RenderWith(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = -1,
            LastUpdate = DateTime.UtcNow,
        }, out _);

        Assert.IsNotNull(FrameTimeStore.TryReadFresh(TimeSpan.FromSeconds(5)));
        FrameTimeStore.Reset();
    }

    [TestMethod]
    public void Render_TrackedIdleProcess_ShowsZeroWithoutThrowing()
    {
        RenderWith(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = 4321,
            ProcessName = "game.exe",
            Fps = 0,
            LastUpdate = DateTime.UtcNow,
        }, out _);

        Assert.IsNotNull(FrameTimeStore.TryReadFresh(TimeSpan.FromSeconds(5)));
        FrameTimeStore.Reset();
    }

    [TestMethod]
    public void Render_FullMetrics_ShowsAllEightCardsWithoutThrowing()
    {
        RenderWith(new FrameTimeSnapshotDto
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
            LastUpdate = DateTime.UtcNow,
        }, out _);

        Assert.IsNotNull(FrameTimeStore.TryReadFresh(TimeSpan.FromSeconds(5)));
        FrameTimeStore.Reset();
    }

    [TestMethod]
    public void OnTouch_Tap_TogglesOverlayView()
    {
        var widget = new FrameTimeWidget();
        widget.OnTouch(default, TouchEventType.TouchUp);
        Assert.IsTrue(widget.IsOverlayView, "a tap must switch to the overlay readout");
        widget.OnTouch(default, TouchEventType.TouchUp);
        Assert.IsFalse(widget.IsOverlayView, "a second tap must switch back");
    }

    [TestMethod]
    public void OnTouch_TouchDown_DoesNotToggle()
    {
        var widget = new FrameTimeWidget();
        widget.OnTouch(default, TouchEventType.TouchDown);
        Assert.IsFalse(widget.IsOverlayView);
    }

    [TestMethod]
    public void Render_OverlayView_RendersLinesWithoutThrowing()
    {
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
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
            PresentModeId = 4,
            LastUpdate = DateTime.UtcNow,
        });

        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var widget = new FrameTimeWidget { IsOverlayView = true };
        widget.Render(surface.Canvas, new SKRect(0, 0, 1016, 592));
        FrameTimeStore.Reset();

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void Render_OverlayView_SmallSize_RendersWithoutThrowing()
    {
        FrameTimeStore.UpdateFromDto(new FrameTimeSnapshotDto
        {
            IsAvailable = true,
            CaptureHealthy = true,
            ProcessId = 4321,
            ProcessName = "game.exe",
            Fps = 60.0,
            Low1PercentFps = 55.0,
            Low01PercentFps = 50.0,
            GpuBusyPercent = 30.0,
            CpuFrameTimeMs = 2.0,
            DisplayedFps = 60.0,
            DroppedFrames = 0,
            GpuTimeMs = 3.0,
            PresentModeId = 4,
            LastUpdate = DateTime.UtcNow,
        });

        using var smallSurface = SKSurface.Create(new SKImageInfo(200, 160));
        var widget = new FrameTimeWidget { IsOverlayView = true };
        widget.Render(smallSurface.Canvas, new SKRect(0, 0, 200, 160));
        FrameTimeStore.Reset();

        Assert.IsNotNull(smallSurface);
    }
}
