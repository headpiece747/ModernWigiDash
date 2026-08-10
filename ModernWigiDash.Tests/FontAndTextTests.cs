using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class FontAndTextTests
{
    [TestMethod]
    public void WidgetPropertyType_ContainsFontAndIconEditors()
    {
        Assert.IsTrue(Enum.IsDefined(typeof(WidgetPropertyType), WidgetPropertyType.Font));
        Assert.IsTrue(Enum.IsDefined(typeof(WidgetPropertyType), WidgetPropertyType.Icon));
    }

    [TestMethod]
    public void FontHelper_ListsSystemFontFamiliesOnce()
    {
        string[] families = FontHelper.GetAllFamilies();
        Assert.IsNotNull(families);
        Assert.IsTrue(families.Length > 0);
        Assert.AreEqual(families.Length, families.Select(f => f.ToUpperInvariant()).Distinct().Count());
    }

    [TestMethod]
    public void FontHelper_GetTypeface_ResolvesNamedSystemFamilies()
    {
        var arial = FontHelper.GetTypeface("Arial", SKFontStyle.Normal);
        SKTypeface direct = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
        Assert.IsNotNull(arial);
        Assert.AreNotEqual(IntPtr.Zero, arial.Handle);
        Assert.AreEqual(direct.FamilyName, arial.FamilyName, true);
    }

    [TestMethod]
    public void FontHelper_GetTypefaceForCodepoint_ResolvesEmojiFallback()
    {
        // Latin 'A' should resolve to a valid typeface (Geist or system fallback)
        var latinTf = FontHelper.GetTypefaceForCodepoint('A', SKFontStyle.Normal);
        Assert.IsNotNull(latinTf);
        Assert.AreNotEqual(IntPtr.Zero, latinTf.Handle);

        // Emoji 😀 (U+1F600) should resolve to a valid fallback typeface
        var emojiTf = FontHelper.GetTypefaceForCodepoint(0x1F600, SKFontStyle.Normal);
        Assert.IsNotNull(emojiTf);
        Assert.AreNotEqual(IntPtr.Zero, emojiTf.Handle);
    }

    [TestMethod]
    public void FontHelper_GetTypefaceForCodepoint_HonorsPreferredTypeface()
    {
        var arial = FontHelper.GetTypeface("Arial", SKFontStyle.Normal);
        Assert.IsNotNull(arial);
        Assert.AreNotEqual(IntPtr.Zero, arial.Handle);

        var resolved = FontHelper.GetTypefaceForCodepoint('A', SKFontStyle.Normal, arial);
        Assert.AreEqual(arial.FamilyName, resolved.FamilyName, true);
    }

    [TestMethod]
    public void FontHelper_GetTypefaceForCodepoint_PreferredWithoutGlyph_FallsBack()
    {
        var arial = FontHelper.GetTypeface("Arial", SKFontStyle.Normal);
        var emoji = FontHelper.GetTypefaceForCodepoint(0x1F600, SKFontStyle.Normal, arial);
        Assert.IsNotNull(emoji);
        Assert.AreNotEqual(IntPtr.Zero, emoji.Handle);
    }

    [TestMethod]
    public void FontHelper_GetTextRuns_RespectsPreferredTypeface()
    {
        var arial = FontHelper.GetTypeface("Arial", SKFontStyle.Normal);
        var runs = FontHelper.GetTextRuns("Hello", SKFontStyle.Normal, arial);
        Assert.AreEqual(1, runs.Count);
        Assert.AreEqual(arial.FamilyName, runs[0].Typeface.FamilyName, true);
    }

    [TestMethod]
    public void FontHelper_MeasureTextWithFallback_MatchesDirectFontMeasure()
    {
        var arial = FontHelper.GetTypeface("Arial", SKFontStyle.Normal);
        using var font = FontHelper.CreateFont(arial, 24f);
        float direct = font.MeasureText("Hello");
        float fallback = FontHelper.MeasureTextWithFallback("Hello", font);
        Assert.AreEqual(direct, fallback, 0.01f);
    }

    [TestMethod]
    public void FontHelper_GetAllFamilies_IncludesGeist()
    {
        string[] families = FontHelper.GetAllFamilies();
        Assert.IsTrue(families.Contains("Geist"), "Geist must be listed so the inspector can select the default font.");
    }

    [TestMethod]
    public void TextLabelWidget_Defaults_MatchSpec()
    {
        var widget = new TextLabelWidget();
        Assert.AreEqual("Your text here", widget.Text);
        Assert.AreEqual("Geist", widget.FontFamily);
        Assert.AreEqual(32, widget.FontSize);
        Assert.AreEqual("#FAFAFA", widget.TextColorHex);
        Assert.AreEqual("Center", widget.Alignment);
        Assert.AreEqual("#00000000", widget.BackgroundHex);
    }

    [TestMethod]
    public void TextLabelWidget_ProvidesFontOptions()
    {
        var widget = new TextLabelWidget();
        var provider = (IWidgetPropertyOptionsProvider)widget;
        var options = provider.GetPropertyOptions(nameof(widget.FontFamily));
        Assert.IsTrue(options.Count > 0);
        Assert.AreEqual(options[0].Value, options[0].DisplayName);
        Assert.AreEqual(0, provider.GetPropertyOptions("UnknownProperty").Count);
    }

    [TestMethod]
    public void TextLabelWidget_RendersMultiLineTextWithoutExceptions()
    {
        var widget = new TextLabelWidget
        {
            Text = "Line one\nLine two is a longer line that should wrap",
            FontFamily = "Arial",
            FontSize = 24,
            Alignment = "Center"
        };
        using var surface = SKSurface.Create(new SKImageInfo(400, 200));
        var canvas = surface.Canvas;
        widget.Render(canvas, new SKRect(0, 0, 400, 200));
        Assert.IsNotNull(surface);
    }
}
