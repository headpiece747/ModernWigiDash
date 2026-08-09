using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>The cached hex-color parsing helper every widget color site now
/// routes through — parse semantics and fallback declared once.</summary>
[TestClass]
public class ColorOfTests
{
    private sealed class TestWidget : ModernWidgetBase
    {
        public SKColor Get(string hex, SKColor fallback) => ColorOf(hex, fallback);
        public override void Render(SKCanvas canvas, SKRect bounds) { }
    }

    [TestMethod]
    public void ColorOf_ValidHex_Parses()
    {
        var widget = new TestWidget();

        Assert.AreEqual(new SKColor(0xF5, 0x9E, 0x0B), widget.Get("#F59E0B", SKColors.White));
    }

    [TestMethod]
    public void ColorOf_InvalidHex_UsesFallback()
    {
        var widget = new TestWidget();

        Assert.AreEqual(SKColors.White, widget.Get("not-a-color", SKColors.White));
    }

    [TestMethod]
    public void ColorOf_RepeatedCalls_SameResult()
    {
        var widget = new TestWidget();

        SKColor first = widget.Get("#F59E0B", SKColors.White);
        SKColor second = widget.Get("#F59E0B", SKColors.White);

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void ColorOf_DistinctValues_DistinctResults()
    {
        var widget = new TestWidget();

        Assert.AreNotEqual(
            widget.Get("#F59E0B", SKColors.White),
            widget.Get("#10B981", SKColors.White));
    }
}
