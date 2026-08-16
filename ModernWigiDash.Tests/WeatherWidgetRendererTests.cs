using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pixel-pattern tests for <see cref="WeatherWidgetRenderer"/>: the pill-width
/// measurement contract, the shrink re-measure and hero fit/auto-scale
/// branches, and the per-mode draw regions - pinned on a real SKSurface (the
/// SvgIconHelperTests pattern) instead of through the widget.
/// </summary>
[TestClass]
public class WeatherWidgetRendererTests
{
    private static readonly SKColor Background = SKColors.Black;
    private static readonly SKColor Accent = SKColors.Red;

    // The model is built directly (the class is internal, reachable via
    // InternalsVisibleTo) so the renderer's branches are drivable without the
    // widget's orchestration.
    private static WeatherRenderModel CreateModel(
        string mainTemp = "22.5°C",
        int weatherCode = 2,
        IReadOnlyList<string>? metrics = null,
        int dailyCount = 0,
        int hourlyCount = 0)
    {
        string[] metricList = metrics?.ToArray() ?? [];
        var ranges = new string[dailyCount];
        var highLows = new string[dailyCount];
        var hourlyTemps = new string[hourlyCount];
        for (int i = 0; i < dailyCount; i++)
        {
            ranges[i] = WeatherPresentation.ForecastRangeText(20 + i, 10 + i, "°C");
            highLows[i] = WeatherPresentation.DailyHighLowText(20 + i, 10 + i, "°C");
        }
        for (int i = 0; i < hourlyCount; i++)
        {
            hourlyTemps[i] = WeatherPresentation.FormatTemp(15 + i, "°C");
        }

        return new WeatherRenderModel
        {
            DataVersion = 1,
            WeatherCode = weatherCode,
            Daily = Enumerable.Range(0, dailyCount).Select(i => new DailyForecastItem($"Day{i}", 20 + i, 10 + i, 1)).ToArray(),
            Hourly = Enumerable.Range(0, hourlyCount).Select(i => new HourlyForecastItem($"{i}:00", 15 + i, 1)).ToArray(),
            Display = new WeatherDisplay(mainTemp, metricList, ranges, highLows, hourlyTemps),
            ShowForecast = dailyCount > 0,
        };
    }

    private static bool IsBright(SKColor c) => Math.Max(c.Red, Math.Max(c.Green, c.Blue)) > 90;
    private static bool IsWhite(SKColor c) => c.Red > 90 && c.Green > 90 && c.Blue > 90;
    private static bool IsAccent(SKColor c) => c.Red > 90 && c.Green < 80 && c.Blue < 80;

    private static int CountPixels(SKSurface surface, SKRect region, Func<SKColor, bool> match)
    {
        var pixmap = surface.PeekPixels();
        int count = 0;
        for (int y = (int)region.Top; y < (int)region.Bottom; y++)
        {
            for (int x = (int)region.Left; x < (int)region.Right; x++)
            {
                if (match(pixmap.GetPixelColor(x, y))) count++;
            }
        }
        return count;
    }

    private static void AssertRegionHas(SKSurface surface, SKRect region, Func<SKColor, bool> match, string message)
        => Assert.IsTrue(CountPixels(surface, region, match) > 0, message);

    /// <summary>The bounding box of all pixels matching <paramref name="match"/>
    /// inside <paramref name="region"/> (or the whole surface), or
    /// <see cref="SKRect.Empty"/> when none exist.</summary>
    private static SKRect DrawnSpan(SKSurface surface, Func<SKColor, bool> match, SKRect? region = null)
    {
        var pixmap = surface.PeekPixels();
        int width = pixmap.Info.Width, height = pixmap.Info.Height;
        int left = region is { } r ? (int)Math.Max(0, r.Left) : 0;
        int top = region is { } t ? (int)Math.Max(0, t.Top) : 0;
        int right = region is { } rr ? (int)Math.Min(width, rr.Right) : width;
        int bottom = region is { } bb ? (int)Math.Min(height, bb.Bottom) : height;
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                if (match(pixmap.GetPixelColor(x, y)))
                {
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }
        return minX == int.MaxValue ? SKRect.Empty : new SKRect(minX, minY, maxX, maxY);
    }

    // -- MeasurePillWidths ----------------------------------------------------

    [TestMethod]
    public void MeasurePillWidths_TextPlusTwoPads_ExactFormula()
    {
        // The contract the draw path and the model share: text width + 2x the
        // horizontal padding, per pill. Pinning the pad delta exactly catches
        // a regression that drops or doubles the padding.
        string[] metrics = ["Feels: 22°", "Humidity: 87%", "Wind: 12 km/h", "H:25° L:16°"];

        float[] bare = WeatherWidgetRenderer.MeasurePillWidths(metrics, 12f, 0f);
        float[] padded = WeatherWidgetRenderer.MeasurePillWidths(metrics, 12f, 5f);

        Assert.AreEqual(metrics.Length, padded.Length);
        for (int i = 0; i < metrics.Length; i++)
        {
            Assert.AreEqual(bare[i] + 10f, padded[i], 0.001f, $"pill {i} must add exactly 2x5px padding");
        }
    }

    [TestMethod]
    public void MeasurePillWidths_EmptyMetrics_ReturnsEmptyArray()
    {
        Assert.AreEqual(0, WeatherWidgetRenderer.MeasurePillWidths([], 12f, 4f).Length);
    }

    [TestMethod]
    public void MeasurePillWidths_LongerTextOrLargerFont_Widens()
    {
        // Monotonicity pins that the measurement follows the text and the
        // font size - a cached or stale width would break both directions.
        string[] shortPill = ["Feels: 22°"];
        string[] longPill = ["Humidity: 87%"];

        Assert.IsTrue(
            WeatherWidgetRenderer.MeasurePillWidths(longPill, 12f, 4f)[0]
            > WeatherWidgetRenderer.MeasurePillWidths(shortPill, 12f, 4f)[0],
            "a longer string must measure wider at the same font size");

        Assert.IsTrue(
            WeatherWidgetRenderer.MeasurePillWidths(longPill, 20f, 4f)[0]
            > WeatherWidgetRenderer.MeasurePillWidths(longPill, 10f, 4f)[0],
            "a larger font must measure wider for the same string");
    }

    // -- Detailed: pill-shrink re-measure -------------------------------------

    [TestMethod]
    public void RenderDetailed_OverflowingPills_ShrinkReMeasureKeepsEveryPillInside()
    {
        // The shrink branch (RenderMetricPills) fires when the un-shrunk pills
        // overflow the strip: it re-measures at a smaller font + padding so
        // the strip fits. Pinned on pixels: every pill's text must be fully
        // visible inside the content width - an unshrunk layout would clip the
        // first/last pill at the canvas edges.
        using var surface = SKSurface.Create(new SKImageInfo(300, 300));
        surface.Canvas.Clear(Background);
        float sx = 300f / WeatherLayout.DesignWidth;
        float sy = 300f / WeatherLayout.DesignHeight;
        float s = Math.Min(sx, sy);
        string[] metrics = ["Feels: 22°", "Humidity: 87%", "Wind: 12 km/h", "H:25° L:16°"];
        var model = CreateModel(metrics: metrics);
        model.MetricWidths = WeatherWidgetRenderer.MeasurePillWidths(metrics, WeatherLayout.PillFontSize(s), WeatherLayout.PillPadX(s));

        // Preconditions: the pills must overflow (so the branch fires) but not
        // so far that the 7px legibility floor bites (the re-measured strip
        // would then still clip). Both are measured, so a font-metric change
        // fails here with a clear message instead of a pixel mystery.
        float total = model.MetricWidths.Sum() + (metrics.Length - 1) * WeatherLayout.PillGap(s);
        float shrink = WeatherLayout.MetricPillShrinkScale(total, 300f);
        Assert.IsTrue(shrink < 1f, "precondition: the pills must overflow so the shrink branch fires");
        Assert.IsTrue(WeatherLayout.PillFontSize(s) * shrink >= 7f,
            "precondition: the legibility floor must not bite - the re-measured strip then fits");

        using (var renderer = new WeatherWidgetRenderer())
        {
            renderer.RenderDetailed(surface.Canvas, new SKRect(0, 0, 300, 300), Accent, SKColors.White, SKColors.White, sx, sy, model);
        }

        // The pill band: heroBottom ~ 255, pillY ~ 259.5, height ~ 28.4 - the
        // hero text stays above ~200, so rows 262..285 contain only pills.
        var band = new SKRect(0, 262, 300, 285);
        int pillPixels = CountPixels(surface, band, IsBright);
        Assert.IsTrue(pillPixels > 0, "the pill strip must be drawn");

        int rightmost = -1, leftmost = int.MaxValue;
        var pixmap = surface.PeekPixels();
        for (int y = 262; y < 285; y++)
        {
            for (int x = 0; x < 300; x++)
            {
                if (IsBright(pixmap.GetPixelColor(x, y)))
                {
                    rightmost = Math.Max(rightmost, x);
                    leftmost = Math.Min(leftmost, x);
                }
            }
        }
        Assert.IsTrue(leftmost >= 3, "the first pill must not be clipped at the left edge");
        Assert.IsTrue(rightmost <= 296, "the last pill must not be clipped at the right edge");
        Assert.IsTrue(rightmost >= 270, "the re-measured strip must span the full width - the last pill must be drawn near the right edge");
    }

    // -- Detailed: hero fit-scale branch --------------------------------------

    [TestMethod]
    public void RenderDetailed_ShortHero_FitScaleRefetchesConditionFontSmaller()
    {
        // The fit-scale branch: when the temp/condition stack exceeds 85% of
        // the hero height, both fonts are re-fetched proportionally smaller.
        // A tall sy (spacing 20) inflates the stack so the branch fires hard at
        // the hero-height floor (35px, where the condition font would otherwise
        // sit at its 9px clamp floor). Pinned on pixels: the drawn condition
        // width must match the floor width scaled by the branch's own factor
        // (computed from the same cached fonts the renderer measures), and the
        // canvas is tall enough that the baseline math keeps the text inside.
        using var surface = SKSurface.Create(new SKImageInfo(406, 90));
        surface.Canvas.Clear(Background);
        var model = CreateModel();
        model.ShowForecast = false;

        // The branch's inputs at the 35px hero-height floor: tempSize and
        // descSize are the renderer's clamps, spacing is 2x sy.
        const float heroHeight = 35f;
        const float spacing = 20f; // 2 x sy
        var tempFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 15.75f);
        var descFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 9f);
        tempFont.GetFontMetrics(out var tempMetrics);
        descFont.GetFontMetrics(out var descMetrics);
        float stackHeight = (tempMetrics.Descent - tempMetrics.Ascent) + spacing + (descMetrics.Descent - descMetrics.Ascent);
        float fitScale = WeatherLayout.HeroTextStackShrinkScale(stackHeight, heroHeight);
        Assert.IsTrue(fitScale < 1f, "precondition: the stack must overflow the 85% bound so the branch fires");

        using (var renderer = new WeatherWidgetRenderer())
        {
            renderer.RenderDetailed(surface.Canvas, new SKRect(0, 0, 406, 90), Accent, SKColors.White, SKColors.White, 1f, 10f, model);
        }

        // Measured in the x-band right of the icon: the ⛅ emoji is a COLOR
        // font and paints native orange/white pixels regardless of the icon
        // paint, so only the strip right of the hero icon is clean.
        var rightOfIcon = new SKRect(205, 0, 406, 90);
        float drawnDescWidth = DrawnSpan(surface, IsAccent, rightOfIcon).Width;
        float expectedDescWidth = descFont.MeasureText("Partly Cloudy") * fitScale;

        Assert.IsTrue(drawnDescWidth > 0, "the condition text must be visible");
        Assert.IsTrue(Math.Abs(drawnDescWidth - expectedDescWidth) <= expectedDescWidth * 0.15f,
            $"the fit-scale must re-fetch the condition font to {fitScale:F2}x the floor size (drawn {drawnDescWidth:F1}px vs expected {expectedDescWidth:F1}px)");
    }

    // -- Detailed: narrow-container auto-scale ---------------------------------

    [TestMethod]
    public void RenderDetailed_NarrowContainer_AutoScaleShrinksHeroText()
    {
        // The auto-scale branch: when the hero block (icon + gap + temp) is
        // wider than the container, all hero fonts are scaled down together
        // (floor 0.5). Pinned by comparing the SAME model rendered in a wide
        // container (no auto-scale) against a narrow one (auto-scale): the
        // drawn CONDITION text's height must shrink by exactly the branch's
        // scale factor. The condition is measured (not the temperature) - it
        // is the one hero element whose pixels are accent-colored, while the
        // ⛅ emoji icon paints native orange/white pixels that would
        // contaminate a white scan. The ratio is measured from the same fonts
        // the renderer uses, so a font-metric change cannot fake the check.
        string mainTemp = "77°F";
        float sx = 250f / WeatherLayout.DesignWidth; // narrow container
        float s = Math.Min(sx, 1f);

        // Precondition: the un-shrunk hero block (216px icon + gap + 129.6px
        // temp) must overflow the 250px container, measured with the same
        // font cache the renderer uses.
        var iconFont = FontHelper.GetCachedFont("Segoe UI Emoji", SKFontStyle.Bold, 216f);
        float heroBlockWidth = iconFont.MeasureText("⛅")
            + Math.Clamp(20f * s, 8f, 50f)
            + Math.Max(FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 129.6f).MeasureText(mainTemp),
                FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 45f).MeasureText("Partly Cloudy"));
        float expectedScale = Math.Max(0.5f, 250f / heroBlockWidth);
        Assert.IsTrue(heroBlockWidth > 250f, "precondition: the hero block must overflow so the auto-scale fires");
        Assert.IsTrue(expectedScale < 0.9f, "precondition: the container must force a real shrink, not a rounding tweak");

        float narrowHeight;
        float wideHeight;
        var model = CreateModel(mainTemp: mainTemp); // no pills, no forecast - only the hero
        using (var renderer = new WeatherWidgetRenderer())
        {
            using var wide = SKSurface.Create(new SKImageInfo(800, 296));
            wide.Canvas.Clear(Background);
            renderer.RenderDetailed(wide.Canvas, new SKRect(0, 0, 800, 296), Accent, SKColors.White, SKColors.White, 800f / WeatherLayout.DesignWidth, 1f, model);
            wideHeight = DrawnSpan(wide, IsAccent).Height;

            using var narrow = SKSurface.Create(new SKImageInfo(250, 296));
            narrow.Canvas.Clear(Background);
            renderer.RenderDetailed(narrow.Canvas, new SKRect(0, 0, 250, 296), Accent, SKColors.White, SKColors.White, sx, 1f, model);
            narrowHeight = DrawnSpan(narrow, IsAccent).Height;
        }

        Assert.IsTrue(wideHeight > 40f, "the wide (un-scaled) condition must be legibly large");
        float drawnRatio = narrowHeight / wideHeight;
        Assert.IsTrue(Math.Abs(drawnRatio - expectedScale) < 0.1f,
            $"the auto-scale must re-fetch the hero fonts by its scale factor {expectedScale:F2}, not {drawnRatio:F2}");
    }

    // -- Per-mode draw regions ------------------------------------------------

    [TestMethod]
    public void RenderDetailed_DrawsHeroPillsAndStrip_AtExpectedRegions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        surface.Canvas.Clear(Background);
        string[] metrics = ["Feels: 22°", "Humidity: 87%"];
        var model = CreateModel(metrics: metrics, dailyCount: 3);
        model.MetricWidths = WeatherWidgetRenderer.MeasurePillWidths(metrics, WeatherLayout.PillFontSize(1f), WeatherLayout.PillPadX(1f));

        using (var renderer = new WeatherWidgetRenderer())
        {
            renderer.RenderDetailed(surface.Canvas, new SKRect(0, 0, 406, 296), Accent, SKColors.White, SKColors.White, 1f, 1f, model);
        }

        // Hero: the condition line (accent) sits in the upper band, above the
        // pill strip (~y 176-204) and the forecast strip (~y 216-296).
        AssertRegionHas(surface, new SKRect(0, 0, 406, 170), IsAccent, "the hero condition must be drawn in the upper band");
        AssertRegionHas(surface, new SKRect(0, 170, 406, 210), IsWhite, "the metric pills must be drawn in the pill band");
        AssertRegionHas(surface, new SKRect(0, 216, 406, 296), IsWhite, "the forecast strip must draw its day names/ranges");
        AssertRegionHas(surface, new SKRect(0, 216, 406, 296), IsAccent, "the first strip day name must be drawn in the accent color");
    }

    [TestMethod]
    public void RenderDailyForecast_DrawsRows_WithAccentFirstRow()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        surface.Canvas.Clear(Background);
        var model = CreateModel(dailyCount: 3);

        using (var renderer = new WeatherWidgetRenderer())
        {
            renderer.RenderDailyForecast(surface.Canvas, new SKRect(0, 0, 406, 296), Accent, SKColors.White, SKColors.White, 1f, 1f, model);
        }

        // Three rows of ~98px: the first row's day name is accent, the later
        // rows' names are white, and every row's temperature is accent.
        AssertRegionHas(surface, new SKRect(0, 0, 406, 60), IsAccent, "the first row's day name must be accent");
        AssertRegionHas(surface, new SKRect(0, 60, 406, 296), IsWhite, "the later rows' day names/descriptions must be drawn");
        AssertRegionHas(surface, new SKRect(0, 200, 406, 296), IsAccent, "the last row's temperatures must be drawn in the accent color");
    }

    [TestMethod]
    public void RenderHourlyForecast_DrawsColumns_WithTimeTopAndTempBottom()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        surface.Canvas.Clear(Background);
        var model = CreateModel(hourlyCount: 4);

        using (var renderer = new WeatherWidgetRenderer())
        {
            renderer.RenderHourlyForecast(surface.Canvas, new SKRect(0, 0, 406, 296), Accent, SKColors.White, 1f, 1f, model);
        }

        // Time labels hug the column tops (~y 26); temperatures hug the
        // bottoms (~y 278).
        AssertRegionHas(surface, new SKRect(0, 0, 406, 60), IsWhite, "the time labels must be drawn near the column tops");
        AssertRegionHas(surface, new SKRect(0, 240, 406, 296), IsAccent, "the temperatures must be drawn near the column bottoms");
    }

    [TestMethod]
    public void RenderCurrentOnly_DrawsCenteredHero()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        surface.Canvas.Clear(Background);
        var model = CreateModel();

        using (var renderer = new WeatherWidgetRenderer())
        {
            renderer.RenderCurrentOnly(surface.Canvas, new SKRect(0, 0, 406, 296), Accent, SKColors.White, 1f, 1f, model);
        }

        // The single hero is vertically centered (~y 148): temperature white,
        // condition accent, both inside the middle band.
        AssertRegionHas(surface, new SKRect(0, 80, 406, 220), IsWhite, "the temperature must be drawn in the middle band");
        AssertRegionHas(surface, new SKRect(0, 80, 406, 220), IsAccent, "the condition must be drawn in the middle band");
    }

    [TestMethod]
    public void RenderCompact_DrawsIconAndTemp_AtTheLeft()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        surface.Canvas.Clear(Background);
        var model = CreateModel();

        using (var renderer = new WeatherWidgetRenderer())
        {
            renderer.RenderCompact(surface.Canvas, new SKRect(0, 0, 406, 296), SKColors.White, 1f, 1f, model);
        }

        // Compact draws only the (black, invisible-on-black) icon and the
        // temperature next to it on the left; the right side stays empty.
        AssertRegionHas(surface, new SKRect(30, 100, 200, 200), IsWhite, "the temperature must be drawn left of the container center");
        Assert.AreEqual(0, CountPixels(surface, new SKRect(270, 100, 406, 200), IsWhite),
            "the right side of a Compact render must stay empty");
    }
}
