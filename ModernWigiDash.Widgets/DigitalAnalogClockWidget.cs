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
        string timeStr = ClockPresentation.FormatClockTime(now, TimeFormat);
        string amPmStr = ClockPresentation.AmPm(now, TimeFormat);
        string dateStr = ClockPresentation.Date(now);

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

        using var majorTickPaint = new SKPaint { Color = textColor, StrokeWidth = 3f, IsAntialias = true };
        using var minorTickPaint = new SKPaint { Color = textColor, StrokeWidth = 1.5f, IsAntialias = true };
        for (int i = 0; i < 12; i++)
        {
            float angle = i * 30f * (float)(Math.PI / 180f);
            float x1 = cx + (radius - 12f) * (float)Math.Sin(angle);
            float y1 = cy - (radius - 12f) * (float)Math.Cos(angle);
            float x2 = cx + radius * (float)Math.Sin(angle);
            float y2 = cy - radius * (float)Math.Cos(angle);
            canvas.DrawLine(x1, y1, x2, y2, i % 3 == 0 ? majorTickPaint : minorTickPaint);
        }

        (float hourAngle, float minAngle, float secAngle) = ClockPresentation.HandAngles(now);

        using var hourHandPaint = new SKPaint { Color = textColor, StrokeWidth = 4.5f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        using var minuteHandPaint = new SKPaint { Color = textColor, StrokeWidth = 3f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        using var secondHandPaint = new SKPaint { Color = accentColor, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        DrawHand(canvas, cx, cy, hourAngle, radius * 0.5f, hourHandPaint);
        DrawHand(canvas, cx, cy, minAngle, radius * 0.75f, minuteHandPaint);
        DrawHand(canvas, cx, cy, secAngle, radius * 0.85f, secondHandPaint);

        using var centerDot = new SKPaint { Color = textColor, IsAntialias = true };
        canvas.DrawCircle(cx, cy, 5f, centerDot);
    }

    private static void DrawHand(SKCanvas canvas, float cx, float cy, float angleRad, float length, SKPaint paint)
    {
        float x = cx + length * (float)Math.Sin(angleRad);
        float y = cy - length * (float)Math.Cos(angleRad);
        canvas.DrawLine(cx, cy, x, y, paint);
    }
}
