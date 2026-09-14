using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The shared telemetry renderer's text fitting: the hero value must shrink to
/// stay inside the widget (the on-device bug drew "5600.0 MH" past the edge)
/// and ellipsize past the floor, while short values keep their requested size.
/// </summary>
[TestClass]
public sealed class TelemetryReadingRendererTests
{
    [TestMethod]
    public void FitFont_LongText_ShrinksUntilItFits()
    {
        var font = TelemetryReadingRenderer.FitFont(FontHelper.GeistTypeface, "69641.0 MHz", 60f, 48f, 9f);

        Assert.IsTrue(FontHelper.MeasureTextWithFallback("69641.0 MHz", font) <= 60f, "the fitted font must fit the width");
        Assert.IsTrue(font.Size <= 48f, "the fitted font must not exceed the requested size");
    }

    [TestMethod]
    public void FitFont_ShortText_KeepsTheRequestedSize()
    {
        var font = TelemetryReadingRenderer.FitFont(FontHelper.GeistTypeface, "1", 500f, 48f, 9f);

        Assert.AreEqual(48f, font.Size);
    }

    [TestMethod]
    public void FitFont_TextWiderThanTheFloor_StopsAtTheFloor()
    {
        var font = TelemetryReadingRenderer.FitFont(FontHelper.GeistTypeface, new string('W', 200), 20f, 48f, 9f);

        Assert.AreEqual(9f, font.Size, "the fit never shrinks below the mode's floor (the caller ellipsizes instead)");
    }

    [TestMethod]
    public void GaugeValueMaxWidth_IsInsideTheArcAndFitsALongReading()
    {
        const float gaugeSize = 170f;
        float inner = TelemetryReadingRenderer.GaugeValueMaxWidth(gaugeSize);

        Assert.IsTrue(inner > 0f && inner < gaugeSize, "the value band must sit inside the arc, not the full widget width");
        var font = TelemetryReadingRenderer.FitFont(FontHelper.GeistTypeface, "123456.7 MHz", inner, gaugeSize * 0.2f, 9f);
        Assert.IsTrue(FontHelper.MeasureTextWithFallback("123456.7 MHz", font) <= inner,
            "a long gauge reading must shrink to stay inside the arc");
    }

    [TestMethod]
    public void Render_GraphMode_SparklineStaysBelowTheValueBand()
    {
        using var renderer = new TelemetryReadingRenderer();
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        surface.Canvas.Clear(SKColors.Transparent);
        var display = SystemTelemetryPresentation.Build(
            "CPU Clock", "MHz", 0, 3900f, "", "", "Graph", autoScale: true, maxValue: 100f, decimals: 0f);

        // A few frames so the sparkline path is built (history >= 2).
        for (int i = 0; i < 4; i++)
        {
            renderer.Render(surface.Canvas, new SKRect(0, 0, 406, 296), display, 3900f + i, 0, 0, SKColors.Orange, SKColors.White);
        }

        // The value band under the header holds only the right-aligned value.
        // The sparkline must start below it, otherwise a near-flat graph fills
        // this region across the width.
        var pixels = surface.PeekPixels();
        int painted = 0;
        for (int y = 46; y < 66; y++)
        {
            for (int x = 16; x < 250; x++)
            {
                if (pixels.GetPixelColor(x, y).Alpha != 0)
                {
                    painted++;
                }
            }
        }

        Assert.AreEqual(0, painted, "the sparkline must not paint in the value band (the graph stays under the text)");
    }

    [TestMethod]
    public void Render_ValueMode_LongReadingWithUnit_StaysInsideTheWidgetAtEverySize()
    {
        foreach (var (width, height) in new[] { (203, 148), (406, 296), (1016, 592) })
        {
            using var renderer = new TelemetryReadingRenderer();
            using var surface = SKSurface.Create(new SKImageInfo(width, height));
            surface.Canvas.Clear(SKColors.Transparent);
            var display = SystemTelemetryPresentation.Build(
                "A very long sensor label",
                "Kilometres per hour",
                0,
                123456.7f,
                displayLabelOverride: "",
                unitOverride: "",
                displayMode: "Value",
                autoScale: true,
                maxValue: 100f,
                decimals: 1f);

            renderer.Render(surface.Canvas, new SKRect(0, 0, width, height), display, 123456.7f, 0, 0, SKColors.Orange, SKColors.White);

            // The renderer keeps a 16px side pad; unfitted text would paint over
            // it. Scan the hero-value band (the middle third).
            var pixels = surface.PeekPixels();
            int rightmost = -1;
            for (int x = width - 1; x >= 0 && rightmost < 0; x--)
            {
                for (int y = height / 3; y < height * 2 / 3; y++)
                {
                    if (pixels.GetPixelColor(x, y).Alpha != 0)
                    {
                        rightmost = x;
                        break;
                    }
                }
            }

            Assert.IsTrue(rightmost <= width - 16, $"{width}x{height}: the hero value must stay inside the pad (rightmost painted x={rightmost})");
        }
    }
}
