using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// DigitalAnalogClockWidget: the 12H/24H formatting (pure, via the internal
/// helper) and the render modes driven through the injectable clock.
/// </summary>
[TestClass]
public class DigitalAnalogClockWidgetTests
{
    /// <summary>Fixed local time so the render path is deterministic.</summary>
    private sealed class FixedLocalClock : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedLocalClock(DateTimeOffset localNow) => _utcNow = localNow.ToUniversalTime();

        // TimeProvider.GetLocalNow is not virtual — it derives from GetUtcNow
        // via the machine timezone, which is fine for the pixel-level render
        // assertions these tests make.
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private static readonly DateTimeOffset Afternoon = new(2026, 8, 7, 13, 37, 5, TimeSpan.FromHours(2));

    private static DigitalAnalogClockWidget CreateWidget(DateTimeOffset localNow, string timeFormat = "12H") => new()
    {
        Clock = new FixedLocalClock(localNow),
        TimeFormat = timeFormat
    };

    private static SKSurface CreateSurface() => SKSurface.Create(new SKImageInfo(203, 148));

    [TestMethod]
    public void FormatClockTime_TwelveHour_UsesAmPmHour()
    {
        var now = new DateTime(2026, 8, 7, 13, 37, 5, DateTimeKind.Unspecified);

        Assert.AreEqual("01:37", DigitalAnalogClockWidget.FormatClockTime(now, "12H"));
        Assert.AreEqual("13:37", DigitalAnalogClockWidget.FormatClockTime(now, "24H"));
    }

    [TestMethod]
    public void FormatClockTime_TwelveHour_MidnightRollsToTwelve()
    {
        var midnight = new DateTime(2026, 8, 7, 0, 5, 0, DateTimeKind.Unspecified);

        Assert.AreEqual("12:05", DigitalAnalogClockWidget.FormatClockTime(midnight, "12H"));
        Assert.AreEqual("00:05", DigitalAnalogClockWidget.FormatClockTime(midnight, "24H"));
    }

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
}
