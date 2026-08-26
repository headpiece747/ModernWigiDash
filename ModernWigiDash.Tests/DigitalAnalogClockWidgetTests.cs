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
    public void Render_DigitalMode_ShowSeconds_DrawsTime()
    {
        var widget = CreateWidget(Afternoon, "12H");
        widget.ShowSeconds = true;

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        // The seconds variant draws a smaller 8-char string, so the exact
        // center pixel may fall between glyphs: scan the time row instead.
        var pixels = surface.PeekPixels();
        bool painted = false;
        for (int x = 30; x < 174 && !painted; x += 2)
        {
            painted = pixels.GetPixelColor(x, 74) != SKColors.Transparent;
        }
        Assert.IsTrue(painted, "The digital clock with seconds must paint output");
    }

    [TestMethod]
    public void Render_ShowSecondsToggle_ReformatsWithinTheSameSecond()
    {
        // The memo keys on the seconds toggle: flipping it within the same
        // second must reformat the time string (and re-fit the font), not
        // serve the stale 5-char string. A fixed clock + surface makes the
        // pixel diff deterministic.
        var widget = CreateWidget(Afternoon, "12H");

        using var surface = CreateSurface();
        var bounds = new SKRect(0, 0, 203, 148);
        surface.Canvas.Clear(SKColors.Transparent);
        widget.Render(surface.Canvas, bounds);
        var before = SampleTimeRegion(surface);

        widget.ShowSeconds = true;
        surface.Canvas.Clear(SKColors.Transparent);
        widget.Render(surface.Canvas, bounds);
        var after = SampleTimeRegion(surface);

        Assert.IsFalse(before.SequenceEqual(after), "toggling seconds within the same second must change the rendered time");
    }

    /// <summary>Scans the time region's pixel colors (the rows the time string,
    /// the AM/PM badge, and the date line draw on): two renders with a fixed
    /// clock differ here only when the formatted string or its fit changed.
    /// The region scan (instead of one row) is font-size robust: the seconds
    /// variant draws a smaller string whose glyph extent does not always
    /// reach a single fixed row.</summary>
    private static List<byte> SampleTimeRegion(SKSurface surface)
    {
        var colors = surface.PeekPixels();
        var samples = new List<byte>(512);
        for (int y = 25; y < 100; y += 3)
        {
            for (int x = 10; x < 194; x += 4)
            {
                SKColor c = colors.GetPixelColor(x, y);
                samples.Add(c.Red);
                samples.Add(c.Green);
                samples.Add(c.Blue);
            }
        }
        return samples;
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
