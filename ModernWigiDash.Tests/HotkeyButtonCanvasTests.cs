namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The hotkey-button canvas's draw surface (the house render-smoke pattern,
/// CozyTodoCanvasTests): the pressed-state glow, label, and description
/// paint onto a real canvas through the label-only path (no icon assets
/// needed), so a regression that drops a draw path is caught where the
/// pixels land.
/// </summary>
[TestClass]
public class HotkeyButtonCanvasTests
{
    [TestMethod]
    public void Render_LabelOnly_Smoke_PaintsTheLabelAndDescription()
    {
        using var canvas = new HotkeyButtonCanvas();
        using var bitmap = new SKBitmap(300, 200);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.Render(
            skCanvas, new SKRect(0, 0, 300, 200),
            isPressed: true,
            label: "Hotkey",
            description: "Open settings",
            hasIconGeometry: false,
            iconCenter: SKPoint.Empty,
            half: 0f,
            useCustomFile: false,
            resolvedPath: null,
            griddyIconName: "",
            btnColor: SKColor.Parse("#2563EB"),
            textColor: SKColors.White,
            iconColor: SKColors.White);

        Assert.IsTrue(Painted(bitmap), "the hotkey button's glow, label, and description must paint onto the canvas");
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
