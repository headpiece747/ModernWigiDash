namespace ModernWigiDash.Tests;

/// <summary>
/// DigitalAnalogClockWidget render modes, driven through the injectable clock.
/// The pure display rules (formatting, hand angles) live in
/// <see cref="ClockPresentation"/> and are covered by
/// ClockStopwatchTickerPresentationTests.
/// </summary>
[TestClass]
public class DigitalAnalogClockWidgetTests
{
    private static readonly DateTimeOffset Afternoon = new(2026, 8, 7, 13, 37, 5, TimeSpan.FromHours(2));

    private static DigitalAnalogClockWidget CreateWidget(DateTimeOffset localNow, string timeFormat = "12H") => new()
    {
        // GetLocalNow derives from GetUtcNow via the machine timezone, which is
        // fine for the pixel-level render assertions these tests make.
        Clock = new FakeTimeProvider(localNow.ToUniversalTime()),
        TimeFormat = timeFormat
    };

    private static SKSurface CreateSurface() => SKSurface.Create(new SKImageInfo(203, 148));

    [TestMethod]
    public void Render_DigitalMode_TwelveHour_DrawsTime()
    {
        var widget = CreateWidget(Afternoon, "12H");

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        var pixel = surface.PeekPixels().GetPixelColor(101, 74);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The digital clock must paint output");
    }

    [TestMethod]
    public void Render_DigitalMode_TwentyFourHour_DrawsTime()
    {
        var widget = CreateWidget(Afternoon, "24H");

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        var pixel = surface.PeekPixels().GetPixelColor(101, 74);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The digital clock must paint output");
    }

    [TestMethod]
    public void Render_AnalogMode_DrawsHands()
    {
        var widget = CreateWidget(Afternoon);
        widget.ClockMode = "Analog";

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        var pixel = surface.PeekPixels().GetPixelColor(101, 74);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The analog clock must paint output");
    }

    [TestMethod]
    public void Render_ShowDateOff_RendersWithoutDate()
    {
        var widget = CreateWidget(Afternoon);
        widget.ShowDate = false;

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        var pixel = surface.PeekPixels().GetPixelColor(101, 74);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The clock must paint output without the date badge");
    }

    [TestMethod]
    public void Render_CustomColors_ExecutesWithoutExceptions()
    {
        var widget = CreateWidget(Afternoon);
        widget.TextColorHex = "#FFCD85";
        widget.AccentColorHex = "#22C55E";

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));
        Assert.IsNotNull(surface);
    }
}
