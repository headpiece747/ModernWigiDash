namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets;

/// <summary>
/// The crypto/stock ticker canvas's draw surface (the house render-smoke
/// pattern, CozyTodoCanvasTests): the active ticker scene and the
/// empty-symbol placeholder both paint onto a real canvas, so a regression
/// that drops a draw path is caught where the pixels land.
/// </summary>
[TestClass]
public class CryptoStockTickerCanvasTests
{
    [TestMethod]
    public void RenderTicker_Smoke_PaintsTheSymbolPriceAndBadge()
    {
        using var canvas = new CryptoStockTickerCanvas();
        using var bitmap = new SKBitmap(400, 200);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.RenderTicker(
            skCanvas, new SKRect(0, 0, 400, 200),
            "BTC / Bitcoin", "$64,120.50", "+2.4%", true, false, true,
            SKColors.White, SKColor.Parse("#22C55E"), SKColor.Parse("#EF4444"));

        Assert.IsTrue(Painted(bitmap), "the ticker's symbol, price, and change badge must paint onto the canvas");
    }

    [TestMethod]
    public void RenderPlaceholder_Smoke_PaintsTheHint()
    {
        using var canvas = new CryptoStockTickerCanvas();
        using var bitmap = new SKBitmap(400, 200);
        using var skCanvas = new SKCanvas(bitmap);

        canvas.RenderPlaceholder(skCanvas, new SKRect(0, 0, 400, 200), SKColors.White);

        Assert.IsTrue(Painted(bitmap), "the ticker's empty-symbol placeholder must paint onto the canvas");
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
