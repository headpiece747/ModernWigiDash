namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The picture canvas's draw surface (the house render-smoke pattern,
/// CozyTodoCanvasTests): the fitted bitmap draw and the source-missing
/// placeholder both paint onto a real canvas, so a regression that drops a
/// draw path (a dead clip, a missing bitmap) is caught where the pixels
/// land.
/// </summary>
[TestClass]
public class PictureCanvasTests
{
    [TestMethod]
    public void RenderImage_Smoke_PaintsTheBitmap()
    {
        using var canvas = new PictureCanvas();
        using var bitmap = new SKBitmap(32, 32, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Red);
        using var target = new SKBitmap(200, 200);
        using var skCanvas = new SKCanvas(target);

        canvas.RenderImage(skCanvas, new SKRect(0, 0, 200, 200), bitmap, "Contain", 12f);

        Assert.IsTrue(Painted(target), "the fitted picture must paint onto the canvas");
    }

    [TestMethod]
    public void RenderPlaceholder_Smoke_PaintsTheGlyph()
    {
        using var canvas = new PictureCanvas();
        using var bitmap = new SKBitmap(200, 200);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.RenderPlaceholder(skCanvas, new SKRect(0, 0, 200, 200), "#FFFFFF", false);

        Assert.IsTrue(Painted(bitmap), "the picture placeholder glyph + hint must paint onto the canvas");
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
