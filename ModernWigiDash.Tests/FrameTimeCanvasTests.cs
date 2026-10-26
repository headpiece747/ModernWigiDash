namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The frame-time canvas's draw surface (the house render-smoke pattern,
/// CozyTodoCanvasTests): the live dashboard scene and the capture-down
/// placeholder both paint onto a real canvas, so a regression that drops a
/// draw path is caught where the pixels land.
/// </summary>
[TestClass]
public class FrameTimeCanvasTests
{
    [TestMethod]
    public void Render_Smoke_PaintsTheDashboard()
    {
        using var canvas = new FrameTimeCanvas();
        using var bitmap = new SKBitmap(400, 300);
        using var skCanvas = new SKCanvas(bitmap);
        var snapshot = new FrameTimeSnapshotDto { IsAvailable = true, CaptureHealthy = true };

        canvas.Render(skCanvas, new SKRect(0, 0, 400, 300), snapshot, false, true, SKColors.Cyan, SKColors.White);

        Assert.IsTrue(Painted(bitmap), "the frame-time dashboard must paint onto the canvas");
    }

    [TestMethod]
    public void RenderOverlayView_Smoke_PaintsTheReadout()
    {
        using var canvas = new FrameTimeCanvas();
        using var bitmap = new SKBitmap(200, 100);
        using var skCanvas = new SKCanvas(bitmap);
        var snapshot = new FrameTimeSnapshotDto { IsAvailable = true, CaptureHealthy = true };

        canvas.Render(skCanvas, new SKRect(0, 0, 200, 100), snapshot, true, false, SKColors.Cyan, SKColors.White);

        Assert.IsTrue(Painted(bitmap), "the frame-time overlay readout must paint onto the canvas");
    }

    [TestMethod]
    public void RenderPlaceholder_Smoke_PaintsTheHint()
    {
        using var canvas = new FrameTimeCanvas();
        using var bitmap = new SKBitmap(400, 300);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.RenderPlaceholder(skCanvas, new SKRect(0, 0, 400, 300), "PresentMon", "Capture unavailable", SKColors.White);

        Assert.IsTrue(Painted(bitmap), "the frame-time placeholder must paint onto the canvas");
    }

    private static bool Painted(SKBitmap bitmap)
    {
        for (int x = 20; x < bitmap.Width - 20; x += 5)
        {
            for (int y = 20; y < bitmap.Height - 20; y += 5)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
