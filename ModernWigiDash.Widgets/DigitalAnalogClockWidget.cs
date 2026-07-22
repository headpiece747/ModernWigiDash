using SkiaSharp;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("clock_modern", "Clock", "Displays the current time and date with digital and analog modes.", "ModernWigiDash", "2.0.0", "Clock & Time", GridSizePreset.Size2x1)]
public class DigitalAnalogClockWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size2x1.ToSize();
    public override SKSize MinimumSize => new SKSize(150, 80);

    [WidgetProperty("Clock Mode", WidgetPropertyType.Choice, "Display mode for the clock", "Digital", "Digital", "Analog", "Hybrid")]
    public string ClockMode { get; set; } = "Digital";

    [WidgetProperty("Time Format", WidgetPropertyType.Choice, "12 or 24 hour format", "12H", "12H", "24H")]
    public string TimeFormat { get; set; } = "12H";

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color for typography or hands", "#E53935")]
    public string AccentColorHex { get; set; } = "#E53935"; // Material 3 Red

    [WidgetProperty("Show Date", WidgetPropertyType.Boolean, "Display calendar date badge", true)]
    public bool ShowDate { get; set; } = true;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        using var bgPaint = new SKPaint
        {
            Color = new SKColor(31, 34, 50, 230),
            IsAntialias = true
        };
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(229, 57, 53, 100),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };
        canvas.DrawRoundRect(bounds, 16f, 16f, bgPaint);
        canvas.DrawRoundRect(bounds, 16f, 16f, borderPaint);

        var now = DateTime.Now;
        SKColor.TryParse(AccentColorHex, out var accentColor);
        if (accentColor.Alpha == 0) accentColor = new SKColor(229, 57, 53);

        if (ClockMode == "Analog")
        {
            RenderAnalog(canvas, bounds, now, accentColor);
        }
        else
        {
            RenderDigital(canvas, bounds, now, accentColor);
        }
    }

    private void RenderDigital(SKCanvas canvas, SKRect bounds, DateTime now, SKColor accentColor)
    {
        string timeStr = TimeFormat == "24H" ? now.ToString("HH:mm:ss") : now.ToString("hh:mm:ss");
        string amPmStr = TimeFormat == "24H" ? "" : now.ToString("tt");
        string dateStr = now.ToString("dddd, MMMM dd, yyyy");

        float fontSize = Math.Min(bounds.Width / 5.5f, bounds.Height / 2.2f);
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), fontSize);
        using var textPaint = new SKPaint { Color = new SKColor(244, 239, 244), IsAntialias = true };

        var timeBounds = new SKRect();
        font.MeasureText(timeStr, out timeBounds, textPaint);

        float centerX = bounds.MidX;
        float centerY = ShowDate ? bounds.MidY - 10f : bounds.MidY + (timeBounds.Height / 3f);

        canvas.DrawText(timeStr, centerX - (timeBounds.Width / 2f), centerY, SKTextAlign.Left, font, textPaint);

        if (!string.IsNullOrEmpty(amPmStr))
        {
            using var amFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), fontSize * 0.35f);
            using var amPaint = new SKPaint { Color = accentColor, IsAntialias = true };
            canvas.DrawText(amPmStr, centerX + (timeBounds.Width / 2f) + 8f, centerY - (timeBounds.Height * 0.45f), SKTextAlign.Left, amFont, amPaint);
        }

        if (ShowDate)
        {
            using var dateFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Normal), fontSize * 0.32f);
            using var datePaint = new SKPaint { Color = new SKColor(224, 194, 196), IsAntialias = true };
            var dateBounds = new SKRect();
            dateFont.MeasureText(dateStr, out dateBounds, datePaint);
            canvas.DrawText(dateStr, centerX - (dateBounds.Width / 2f), centerY + (fontSize * 0.5f) + 10f, SKTextAlign.Left, dateFont, datePaint);
        }
    }

    private void RenderAnalog(SKCanvas canvas, SKRect bounds, DateTime now, SKColor accentColor)
    {
        float radius = Math.Min(bounds.Width, bounds.Height) / 2f - 15f;
        float cx = bounds.MidX;
        float cy = bounds.MidY;

        using var facePaint = new SKPaint { Color = new SKColor(255, 255, 255, 10), IsAntialias = true };
        canvas.DrawCircle(cx, cy, radius, facePaint);

        for (int i = 0; i < 12; i++)
        {
            float angle = i * 30f * (float)(Math.PI / 180f);
            float x1 = cx + (radius - 12f) * (float)Math.Sin(angle);
            float y1 = cy - (radius - 12f) * (float)Math.Cos(angle);
            float x2 = cx + radius * (float)Math.Sin(angle);
            float y2 = cy - radius * (float)Math.Cos(angle);
            using var tickPaint = new SKPaint { Color = i % 3 == 0 ? accentColor : new SKColor(244, 239, 244), StrokeWidth = i % 3 == 0 ? 3f : 1.5f, IsAntialias = true };
            canvas.DrawLine(x1, y1, x2, y2, tickPaint);
        }

        float hourAngle = (now.Hour % 12 + now.Minute / 60f) * 30f * (float)(Math.PI / 180f);
        float minAngle = (now.Minute + now.Second / 60f) * 6f * (float)(Math.PI / 180f);
        float secAngle = now.Second * 6f * (float)(Math.PI / 180f);

        DrawHand(canvas, cx, cy, hourAngle, radius * 0.5f, 4.5f, new SKColor(244, 239, 244));
        DrawHand(canvas, cx, cy, minAngle, radius * 0.75f, 3f, new SKColor(244, 239, 244));
        DrawHand(canvas, cx, cy, secAngle, radius * 0.85f, 1.5f, accentColor);

        using var centerDot = new SKPaint { Color = accentColor, IsAntialias = true };
        canvas.DrawCircle(cx, cy, 5f, centerDot);
    }

    private void DrawHand(SKCanvas canvas, float cx, float cy, float angleRad, float length, float width, SKColor color)
    {
        float x = cx + length * (float)Math.Sin(angleRad);
        float y = cy - length * (float)Math.Cos(angleRad);
        using var paint = new SKPaint { Color = color, StrokeWidth = width, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        canvas.DrawLine(cx, cy, x, y, paint);
    }
}
