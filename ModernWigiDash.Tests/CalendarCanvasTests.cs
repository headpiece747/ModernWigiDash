namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The calendar canvas's draw + touch surface (the house render-smoke
/// pattern, CozyTodoCanvasTests): the minimal date card paints onto a real
/// canvas, and an unprimed canvas (never rendered) routes a tap through the
/// gesture state machine without a fault (the default geometry's empty
/// rects resolve nothing).
/// </summary>
[TestClass]
public class CalendarCanvasTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Unspecified);

    [TestMethod]
    public void Render_MinimalCard_Smoke_PaintsTheDateCard()
    {
        using var canvas = new CalendarCanvas();
        using var bitmap = new SKBitmap(200, 180);
        using var skCanvas = new SKCanvas(bitmap);

        // 200x180 resolves to the MinimalDateCard1x1 view (the width/height
        // breakpoint), feed-less, snapshot-less.
        canvas.Render(skCanvas, new SKRect(0, 0, 200, 180), "", "Seasonal", "#10B981", "#E2E8F0", 0, 0, null, Now);

        Assert.IsTrue(Painted(bitmap), "the minimal calendar date card must paint onto the canvas");
    }

    [TestMethod]
    public void OnTouch_UnprimedCanvas_ResolvesToNoHit()
    {
        // The precondition pin: a canvas that has never rendered has the
        // default (empty) geometry, so a tap resolves to nothing, never a
        // fault.
        using var canvas = new CalendarCanvas();

        canvas.OnTouch(new SKPoint(50f, 50f), TouchEventType.TouchDown);
        canvas.OnTouch(new SKPoint(50f, 50f), TouchEventType.TouchUp);

        Assert.IsNull(canvas.DetailEvent, "an unprimed calendar tap must not enter event detail mode");
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
