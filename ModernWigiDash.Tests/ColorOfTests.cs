using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>The cached hex-color parsing helper every widget color site now
/// routes through — parse semantics and fallback declared once.</summary>
[TestClass]
public class ColorOfTests
{
    [TestMethod]
    public void WidgetPalette_Accent_MatchesTheDeclaredDefault()
    {
        // Every AccentColorHex and ButtonColorHex property defaults to the
        // amber #F59E0B; the shared fallback must be that same value, or an
        // invalid hex would render a color that no longer matches the
        // declared default.
        Assert.AreEqual(new SKColor(0xF5, 0x9E, 0x0B), WidgetPalette.Accent);
    }

    [TestMethod]
    public void WidgetPalette_ChatBackground_IsOpaqueLikeTheDefault()
    {
        // The chat background fallback must equal the #0F1117 property default
        // — the old literal drifted to a 235 alpha (slightly translucent).
        Assert.AreEqual(new SKColor(0x0F, 0x11, 0x17), WidgetPalette.ChatBackground);
    }
    [TestMethod]
    public void ColorOf_ValidHex_Parses()
    {
        var widget = new TestWidget();

        Assert.AreEqual(new SKColor(0xF5, 0x9E, 0x0B), widget.GetColor("#F59E0B", SKColors.White));
    }

    [TestMethod]
    public void ColorOf_InvalidHex_UsesFallback()
    {
        var widget = new TestWidget();

        Assert.AreEqual(SKColors.White, widget.GetColor("not-a-color", SKColors.White));
    }

    [TestMethod]
    public void ColorOf_RepeatedCalls_SameResult()
    {
        var widget = new TestWidget();

        SKColor first = widget.GetColor("#F59E0B", SKColors.White);
        SKColor second = widget.GetColor("#F59E0B", SKColors.White);

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void ColorOf_DistinctValues_DistinctResults()
    {
        var widget = new TestWidget();

        Assert.AreNotEqual(
            widget.GetColor("#F59E0B", SKColors.White),
            widget.GetColor("#10B981", SKColors.White));
    }
}
