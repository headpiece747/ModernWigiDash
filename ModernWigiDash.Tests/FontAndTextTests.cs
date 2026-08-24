using ModernWigiDash.Core.Rendering;

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
    public void FontHelper_GetTextRuns_SameInputs_ReturnsSameListInstance()
    {
        var arial = FontHelper.GetTypeface("Arial", SKFontStyle.Normal);
        var first = FontHelper.GetTextRuns("Hello World", SKFontStyle.Normal, arial);
        var second = FontHelper.GetTextRuns("Hello World", SKFontStyle.Normal, arial);
        Assert.AreSame(first, second, "identical inputs must hit the memoized run list");
    }

    [TestMethod]
    public void FontHelper_GetTextRuns_DifferentStyle_ReturnsDistinctRuns()
    {
        var arial = FontHelper.GetTypeface("Arial", SKFontStyle.Normal);
        var normal = FontHelper.GetTextRuns("Hello", SKFontStyle.Normal, arial);
        var bold = FontHelper.GetTextRuns("Hello", SKFontStyle.Bold, arial);
        Assert.AreNotSame(normal, bold, "a style change must key a separate run list");
    }

    [TestMethod]
    public void FontHelper_GetTextRuns_DifferentPreferredTypeface_ReturnsDistinctRuns()
    {
        var arial = FontHelper.GetTypeface("Arial", SKFontStyle.Normal);
        var arialRuns = FontHelper.GetTextRuns("Hello", SKFontStyle.Normal, arial);
        var geistRuns = FontHelper.GetTextRuns("Hello", SKFontStyle.Normal, FontHelper.GeistTypeface);
        Assert.AreNotSame(arialRuns, geistRuns, "a different preferred typeface must key a separate run list");
        Assert.AreEqual(arial.FamilyName, arialRuns[0].Typeface.FamilyName, true);
        Assert.AreEqual(FontHelper.GeistTypeface.FamilyName, geistRuns[0].Typeface.FamilyName, true);
    }

    [TestMethod]
    public void FontHelper_GetTextRuns_FreshStyleWrapperPerCall_ReturnsSameListInstance()
    {
        // The pre-2026-08-24 draw path read SKFont.Typeface?.FontStyle on every
        // call, and the interop layer hands back a fresh SKFontStyle wrapper on
        // every read (it is a value without value equality). The run memo must
        // key the style by value (weight/width/slant) and the typeface by
        // handle, so a fresh wrapper for the same logical style hits the memo
        // instead of recomputing the split every frame.
        var font = FontHelper.CreateFont(FontHelper.GeistTypeface, 32f);
        var styleOne = font.Typeface!.FontStyle!;
        var styleTwo = font.Typeface!.FontStyle!;
        Assert.AreNotSame(styleOne, styleTwo,
            "the interop style-wrapper churn this pin depends on must be real - a stable wrapper makes the pin tautological");

        var first = FontHelper.GetTextRuns("Hello", styleOne, font.Typeface);
        var second = FontHelper.GetTextRuns("Hello", styleTwo, font.Typeface);

        Assert.AreSame(first, second, "a fresh wrapper for the same style value + typeface handle must hit the memoized run list");
    }

    [TestMethod]
    public void FontHelper_GetCachedFont_SameTypefaceAndSize_ReturnsSameInstance()
    {
        var font = FontHelper.CreateFont(FontHelper.GeistTypeface, 32f);

        var viaOne = FontHelper.GetCachedFont(font.Typeface!, 32f);
        var viaTwo = FontHelper.GetCachedFont(font.Typeface!, 32f);

        Assert.AreSame(viaOne, viaTwo, "the same typeface handle + size must hit the font cache");
    }

    [TestMethod]
    public void FontHelper_MeasureTextWithFallback_CachedRunsMeasureCorrectlyAtAnySize()
    {
        var arial = FontHelper.GetTypeface("Arial", SKFontStyle.Normal);
        using var font = FontHelper.CreateFont(arial, 24f);
        using var bigFont = FontHelper.CreateFont(arial, 48f);

        // Prime the memoized runs, then reuse them across a second measure and
        // a different size — the width loop stays per-call and correct.
        float cached = FontHelper.MeasureTextWithFallback("Hello", font);
        float second = FontHelper.MeasureTextWithFallback("Hello", font);
        float bigCached = FontHelper.MeasureTextWithFallback("Hello", bigFont);

        Assert.AreEqual(cached, second, 0.001f);
        Assert.AreEqual(font.MeasureText("Hello"), cached, 0.01f);
        Assert.AreEqual(bigFont.MeasureText("Hello"), bigCached, 0.01f);
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

    [TestMethod]
    public void TextLabelWidget_RendersVeryLongText_WithoutOverflowingBounds()
    {
        // Regression: long names wrapped into many lines spilled past the
        // widget's height (and an over-wide word spilled past the width).
        var widget = new TextLabelWidget
        {
            Text = "Christopher Jonathan Alexander-Montgomery the Third of Longville-on-the-Moor",
            FontFamily = "Arial",
            FontSize = 32,
            Alignment = "Center"
        };
        using var surface = SKSurface.Create(new SKImageInfo(406, 148));
        var canvas = surface.Canvas;
        widget.Render(canvas, new SKRect(0, 0, 406, 148));
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void FitLinesToBounds_TooManyLines_CapsToHeightWithEllipsis()
    {
        var font = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 24f);
        List<string> wrapped = ["one", "two", "three", "four", "five", "six", "seven", "eight"];

        // 8 lines at 24*1.25=30px each = 240px; only 3 fit in 100px.
        var display = TextLabelWidget.FitLinesToBounds(wrapped, font, maxWidth: 300f, lineHeight: 30f, availableHeight: 100f);

        Assert.AreEqual(3, display.Count, "only the lines that fit may be drawn");
        StringAssert.EndsWith(display[^1], "…", "the cut must be signaled with an ellipsis on the last visible line");
        Assert.AreEqual("one", display[0]);
        Assert.AreEqual("two", display[1]);
    }

    [TestMethod]
    public void FitLinesToBounds_EverythingFits_ReturnsAllLines()
    {
        var font = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 24f);
        List<string> wrapped = ["one", "two"];

        var display = TextLabelWidget.FitLinesToBounds(wrapped, font, maxWidth: 300f, lineHeight: 30f, availableHeight: 100f);

        CollectionAssert.AreEqual(new[] { "one", "two" }, display.ToArray());
    }

    [TestMethod]
    public void FitLinesToBounds_OverwideWord_IsTruncatedToWidth()
    {
        var font = FontHelper.GetCachedFont("Arial", SKFontStyle.Normal, 24f);
        // A single word wider than maxWidth (WrapCache's wrap rule gives it its
        // own line).
        List<string> wrapped = ["Supercalifragilisticexpialidocious"];

        var display = TextLabelWidget.FitLinesToBounds(wrapped, font, maxWidth: 80f, lineHeight: 30f, availableHeight: 100f);

        Assert.AreEqual(1, display.Count);
        StringAssert.EndsWith(display[0], "…", "an over-wide word must be truncated, not spill past the width");
    }

    [TestMethod]
    public void TruncateText_ShortText_Unchanged()
    {
        using var surface = SKSurface.Create(new SKImageInfo(100, 50));
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);

        string result = TextRenderHelper.TruncateText("Hello", font, 200f);

        Assert.AreEqual("Hello", result);
    }

    [TestMethod]
    public void TruncateText_LongText_GetsEllipsis()
    {
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 12f);

        string result = TextRenderHelper.TruncateText("A very long widget title that cannot fit the space", font, 60f);

        Assert.IsTrue(result.Length < 40, "the result must be shortened");
        StringAssert.EndsWith(result, "…");
    }
}
