using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

[TestClass]
public class ColorModelTests
{
    // ── HsvToRgb ────────────────────────────────────────────────

    [TestMethod]
    public void HsvToRgb_Red_ReturnsRed()
        => Assert.AreEqual(new RgbaColor(255, 255, 0, 0), ColorConversions.HsvToRgb(new HsvColor(0, 1, 1)));

    [TestMethod]
    public void HsvToRgb_Green_ReturnsGreen()
        => Assert.AreEqual(new RgbaColor(255, 0, 255, 0), ColorConversions.HsvToRgb(new HsvColor(120, 1, 1)));

    [TestMethod]
    public void HsvToRgb_Blue_ReturnsBlue()
        => Assert.AreEqual(new RgbaColor(255, 0, 0, 255), ColorConversions.HsvToRgb(new HsvColor(240, 1, 1)));

    [TestMethod]
    public void HsvToRgb_ZeroSaturation_ReturnsGrayscale()
        => Assert.AreEqual(new RgbaColor(255, 128, 128, 128), ColorConversions.HsvToRgb(new HsvColor(200, 0, 0.5)));

    [TestMethod]
    public void HsvToRgb_ZeroValue_ReturnsBlack()
        => Assert.AreEqual(new RgbaColor(255, 0, 0, 0), ColorConversions.HsvToRgb(new HsvColor(30, 0.5, 0)));

    // ── RgbToHsv ────────────────────────────────────────────────

    [TestMethod]
    public void RgbToHsv_Red_ReturnsRedHsv()
    {
        var hsv = ColorConversions.RgbToHsv(new RgbaColor(255, 255, 0, 0));
        Assert.AreEqual(0, hsv.H, 0.001);
        Assert.AreEqual(1, hsv.S, 0.001);
        Assert.AreEqual(1, hsv.V, 0.001);
    }

    [TestMethod]
    public void RgbToHsv_Black_ReturnsZeroHsv()
    {
        var hsv = ColorConversions.RgbToHsv(new RgbaColor(255, 0, 0, 0));
        Assert.AreEqual(0, hsv.V, 0.001);
    }

    [TestMethod]
    public void HsvToRgb_RgbToHsv_RoundTrips()
    {
        var original = new RgbaColor(255, 245, 158, 11); // #F59E0B
        var roundTripped = ColorConversions.HsvToRgb(ColorConversions.RgbToHsv(original));
        Assert.AreEqual(original.R, roundTripped.R, 1);
        Assert.AreEqual(original.G, roundTripped.G, 1);
        Assert.AreEqual(original.B, roundTripped.B, 1);
        Assert.AreEqual(original.A, roundTripped.A);
    }

    // ── FormatHex ───────────────────────────────────────────────

    [TestMethod]
    public void FormatHex_Opaque_Returns6DigitUppercase()
        => Assert.AreEqual("#F59E0B", ColorConversions.FormatHex(new RgbaColor(255, 245, 158, 11)));

    [TestMethod]
    public void FormatHex_WithAlpha_Returns8DigitUppercase()
        => Assert.AreEqual("#80F59E0B", ColorConversions.FormatHex(new RgbaColor(128, 245, 158, 11)));

    [TestMethod]
    public void FormatHex_ParseColor_RoundTrips()
    {
        var color = new RgbaColor(64, 18, 20, 29);
        var parsed = ThemeSettings.ParseColor(ColorConversions.FormatHex(color));
        Assert.AreEqual(color, parsed);
    }

    // ── Presets ─────────────────────────────────────────────────

    [TestMethod]
    public void PresetPalette_Swatches_AllParse()
        => Assert.IsTrue(PresetPalette.Swatches.All(s => ThemeSettings.ParseColor(s.Hex) is not null));

    [TestMethod]
    public void PresetPalette_Swatches_HasCuratedCount()
        => Assert.AreEqual(12, PresetPalette.Swatches.Count);
}
