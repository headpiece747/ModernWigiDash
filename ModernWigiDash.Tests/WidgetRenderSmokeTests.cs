using ModernWigiDash.Widgets;
using ModernWigiDash.Widgets.Twitch;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class WidgetRenderSmokeTests
{
    [TestMethod]
    public void ColorizedWidgets_RenderWithCustomColors_ExecuteWithoutExceptions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var canvas = surface.Canvas;
        var bounds = new SKRect(0, 0, 406, 296);

        var clock = new DigitalAnalogClockWidget { TextColorHex = "#FFCD85", AccentColorHex = "#22C55E" };
        clock.Render(canvas, bounds);

        var stopwatch = new StopwatchTimerWidget { TextColorHex = "#C6E0FF" };
        stopwatch.Render(canvas, bounds);

        var ticker = new CryptoStockTickerWidget { TextColorHex = "#C6E0FF", PositiveColorHex = "#22C55E", NegativeColorHex = "#EF4444" };
        ticker.Render(canvas, bounds);

        var picture = new PictureAndGifWidget { TextColorHex = "#98B4C8" };
        picture.Render(canvas, bounds);

        var twitch = new TwitchChatStreamWidget { HeaderColorHex = "#FFCD85", MessageColorHex = "#C6E0FF" };
        twitch.Render(canvas, bounds);

        Assert.IsNotNull(surface);
    }
}
