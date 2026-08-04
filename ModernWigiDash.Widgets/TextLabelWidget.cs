using System.Text;
using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("text_label", "Text", "Flexible text label with system fonts, color, size, and alignment.", "ModernWigiDash", "1.0.0", "Utilities", GridSizePreset.Size2x1)]
public class TextLabelWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x1.ToSize();
    public override SKSize MinimumSize => new SKSize(120, 40);

    [WidgetProperty("Text", WidgetPropertyType.Text, "Text to display (supports multiple lines)", "Your text here")]
    public string Text { get; set; } = "Your text here";

    [WidgetProperty("Font Family", WidgetPropertyType.Font, "System font used to render the text", "Geist")]
    public string FontFamily { get; set; } = "Geist";

    [WidgetProperty("Font Size", WidgetPropertyType.Number, "Text size in points", 32)]
    public int FontSize { get; set; } = 32;

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Text color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Alignment", WidgetPropertyType.Choice, "Horizontal text alignment", "Center", "Left", "Center", "Right")]
    public string Alignment { get; set; } = "Center";

    [WidgetProperty("Background Color", WidgetPropertyType.Color, "Rounded-rectangle background (use transparent to disable)", "#00000000")]
    public string BackgroundHex { get; set; } = "#00000000";

    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (propertyName != nameof(FontFamily)) return [];
        return FontCatalog.GetAllFamilies()
            .Select(family => new WidgetPropertyOption(family, family))
            .ToArray();
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor textColor = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;
        SKColor bgColor = SKColor.TryParse(BackgroundHex, out var parsedBg) ? parsedBg : SKColors.Transparent;

        if (bgColor.Alpha > 0)
        {
            using var bgPaint = new SKPaint { Color = bgColor, IsAntialias = true };
            canvas.DrawRoundRect(bounds, 12f, 12f, bgPaint);
        }

        var alignment = Alignment switch
        {
            "Left" => SKTextAlign.Left,
            "Right" => SKTextAlign.Right,
            _ => SKTextAlign.Center
        };

        float fontSize = Math.Max(6f, Math.Min(FontSize, bounds.Height / 2f));
        using var font = FontHelper.CreateFont(FontCatalog.GetTypeface(FontFamily, SKFontStyle.Normal), fontSize);
        using var paint = new SKPaint { Color = textColor, IsAntialias = true };

        float padding = Math.Min(12f, bounds.Width * 0.04f);
        float textWidth = bounds.Width - padding * 2f;

        var wrapped = new List<string>();
        foreach (string rawLine in (Text ?? "").Split('\n'))
        {
            wrapped.AddRange(WrapLine(rawLine, font, textWidth));
        }
        if (wrapped.Count == 0) return;

        float lineHeight = fontSize * 1.25f;
        float totalHeight = wrapped.Count * lineHeight;
        float firstBaseline = bounds.MidY - totalHeight / 2f + fontSize * 0.8f;

        float anchorX = alignment switch
        {
            SKTextAlign.Left => bounds.Left + padding,
            SKTextAlign.Right => bounds.Right - padding,
            _ => bounds.MidX
        };

        for (int i = 0; i < wrapped.Count; i++)
        {
            canvas.DrawTextWithFallback(wrapped[i], anchorX, firstBaseline + i * lineHeight, font, paint, alignment);
        }
    }

    private static List<string> WrapLine(string text, SKFont font, float maxWidth)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            result.Add("");
            return result;
        }

        if (FontHelper.MeasureTextWithFallback(text, font) <= maxWidth)
        {
            result.Add(text);
            return result;
        }

        var current = new StringBuilder();
        foreach (string word in text.Split(' '))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (FontHelper.MeasureTextWithFallback(candidate, font) <= maxWidth)
            {
                current.Clear();
                current.Append(candidate);
            }
            else
            {
                if (current.Length > 0) result.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }
}
