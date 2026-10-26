namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The clock canvas's draw surface (the house render-smoke pattern,
/// CozyTodoCanvasTests): both the digital and the analog scene paint onto a
/// real canvas, so a regression that drops a draw path (an empty font, a
/// dead paint) is caught where the pixels land.
/// </summary>
[TestClass]
public class ClockCanvasTests
{
    private static readonly DateTime Now = new(2026, 10, 25, 10, 15, 30, DateTimeKind.Unspecified);

    [TestMethod]
    public void RenderDigital_Smoke_PaintsTheTimeAndDate()
    {
        using var canvas = new ClockCanvas();
        using var bitmap = new SKBitmap(300, 200);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.RenderDigital(skCanvas, new SKRect(0, 0, 300, 200), Now, "HH:mm:ss", true, true, SKColors.Cyan, SKColors.White);

        Assert.IsTrue(Painted(bitmap), "the digital clock's time + seconds + date must paint onto the canvas");
    }

    [TestMethod]
    public void RenderAnalog_Smoke_PaintsTheFaceAndHands()
    {
        using var canvas = new ClockCanvas();
        using var bitmap = new SKBitmap(300, 200);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.RenderAnalog(skCanvas, new SKRect(0, 0, 300, 200), Now, SKColors.Cyan, SKColors.White);

        Assert.IsTrue(Painted(bitmap), "the analog clock's face, ticks, and hands must paint onto the canvas");
    }

    private static bool Painted(SKBitmap bitmap)
    {
        for (int x = 40; x < bitmap.Width - 40; x += 5)
        {
            for (int y = 40; y < bitmap.Height - 40; y += 5)
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
