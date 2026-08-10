using SkiaSharp;
using ModernWigiDash.Sdk;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("clock_modern", "Clock", Category = "Clock & Time")]
public class DigitalAnalogClockWidget : ModernWidgetBase
{
    public override SKSize DefaultSize => GridSizePreset.Size2x1.ToSize();

    [WidgetProperty("Clock Mode", WidgetPropertyType.Choice, "Display mode for the clock", "Digital", "Digital", "Analog")]
    public string ClockMode { get; set; } = "Digital";

    [WidgetProperty("Time Format", WidgetPropertyType.Choice, "12 or 24 hour format", "12H", "12H", "24H")]
    public string TimeFormat { get; set; } = "12H";

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color for typography or hands", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Primary text, tick, and hand color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Show Date", WidgetPropertyType.Boolean, "Display calendar date badge", true)]
    public bool ShowDate { get; set; } = true;

    /// <summary>Test seam: injectable clock for the rendered time.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var now = Clock.GetLocalNow().LocalDateTime;
        SKColor accentColor = ColorOf(AccentColorHex, new SKColor(135, 0, 0));
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);

        if (ClockMode == "Analog")
        {
            RenderAnalog(canvas, bounds, now, accentColor, textColor);
        }
        else
        {
            RenderDigital(canvas, bounds, now, accentColor, textColor);
        }
    }

    private void RenderDigital(SKCanvas canvas, SKRect bounds, DateTime now, SKColor accentColor, SKColor textColor)
    {
        string timeStr = FormatClockTime(now, TimeFormat);
        string amPmStr = TimeFormat == "24H" ? "" : now.ToString("tt");
        string dateStr = now.ToString("dddd, MMMM dd, yyyy");

        float fontSize = Math.Min(bounds.Width / 5.5f, bounds.Height / 2.2f);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontSize);
        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };

        var timeBounds = new SKRect();
        font.MeasureText(timeStr, out timeBounds, textPaint);

        float centerX = bounds.MidX;
        float centerY = ShowDate ? bounds.MidY - 10f : bounds.MidY + (timeBounds.Height / 3f);

        canvas.DrawTextWithFallback(timeStr, centerX - (timeBounds.Width / 2f), centerY, font, textPaint);

        if (!string.IsNullOrEmpty(amPmStr))
        {
            var amFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontSize * 0.35f);
            using var amPaint = new SKPaint { Color = accentColor, IsAntialias = true };
            canvas.DrawTextWithFallback(amPmStr, centerX + (timeBounds.Width / 2f) + 8f, centerY - (timeBounds.Height * 0.45f), amFont, amPaint);
        }

        if (ShowDate)
        {
            var dateFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontSize * 0.32f);
            using var datePaint = new SKPaint { Color = textColor, IsAntialias = true };
            var dateBounds = new SKRect();
            dateFont.MeasureText(dateStr, out dateBounds, datePaint);
            canvas.DrawTextWithFallback(dateStr, centerX - (dateBounds.Width / 2f), centerY + (fontSize * 0.5f) + 10f, dateFont, datePaint);
        }
    }

    private void RenderAnalog(SKCanvas canvas, SKRect bounds, DateTime now, SKColor accentColor, SKColor textColor)
    {
        float radius = Math.Min(bounds.Width, bounds.Height) / 2f - 15f;
        float cx = bounds.MidX;
        float cy = bounds.MidY;

        using var facePaint = new SKPaint { Color = textColor.WithAlpha(10), IsAntialias = true };
        canvas.DrawCircle(cx, cy, radius, facePaint);

        for (int i = 0; i < 12; i++)
        {
            float angle = i * 30f * (float)(Math.PI / 180f);
            float x1 = cx + (radius - 12f) * (float)Math.Sin(angle);
            float y1 = cy - (radius - 12f) * (float)Math.Cos(angle);
            float x2 = cx + radius * (float)Math.Sin(angle);
            float y2 = cy - radius * (float)Math.Cos(angle);
            using var tickPaint = new SKPaint { Color = textColor, StrokeWidth = i % 3 == 0 ? 3f : 1.5f, IsAntialias = true };
            canvas.DrawLine(x1, y1, x2, y2, tickPaint);
        }

        float hourAngle = (now.Hour % 12 + now.Minute / 60f) * 30f * (float)(Math.PI / 180f);
        float minAngle = (now.Minute + now.Second / 60f) * 6f * (float)(Math.PI / 180f);
        float secAngle = now.Second * 6f * (float)(Math.PI / 180f);

        DrawHand(canvas, cx, cy, hourAngle, radius * 0.5f, 4.5f, textColor);
        DrawHand(canvas, cx, cy, minAngle, radius * 0.75f, 3f, textColor);
        DrawHand(canvas, cx, cy, secAngle, radius * 0.85f, 1.5f, accentColor);

        using var centerDot = new SKPaint { Color = textColor, IsAntialias = true };
        canvas.DrawCircle(cx, cy, 5f, centerDot);
    }

    /// <summary>
    /// Formats the digital clock time for the 12H/24H choice (pure — the
    /// formatting is testable without rendering).
    /// </summary>
    internal static string FormatClockTime(DateTime now, string timeFormat)
        => timeFormat == "24H" ? now.ToString("HH:mm") : now.ToString("hh:mm");

    private static void DrawHand(SKCanvas canvas, float cx, float cy, float angleRad, float length, float width, SKColor color)
    {
        float x = cx + length * (float)Math.Sin(angleRad);
        float y = cy - length * (float)Math.Cos(angleRad);
        using var paint = new SKPaint { Color = color, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        canvas.DrawLine(cx, cy, x, y, paint);
    }
}
