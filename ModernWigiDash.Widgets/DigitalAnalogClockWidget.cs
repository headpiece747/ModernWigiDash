namespace ModernWigiDash.Widgets;

/// <summary>
/// Clock widget: a digital mode (time, optional AM/PM, and the date badge)
/// and an analog mode (face, ticks, and hour/minute/second hands). The
/// display strings and the hand angles come from the ClockPresentation
/// module.
/// </summary>
[WidgetMetadata("clock_modern", "Clock", Category = "Clock & Time", DefaultGridSize = GridSizePreset.Size2x1)]
public class DigitalAnalogClockWidget : ModernWidgetBase
{
    /// <summary>The "Clock Mode" property: display mode for the clock (Digital or Analog).</summary>
    [WidgetProperty("Clock Mode", WidgetPropertyType.Choice, "Display mode for the clock", "Digital", "Digital", "Analog")]
    public string ClockMode { get; set; } = "Digital";

    /// <summary>The "Time Format" property: 12 or 24 hour format.</summary>
    [WidgetProperty("Time Format", WidgetPropertyType.Choice, "12 or 24 hour format", "12H", "12H", "24H")]
    public string TimeFormat { get; set; } = "12H";

    /// <summary>The "Accent Color" property: primary accent color for typography or hands.</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color for typography or hands", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Text Color" property: primary text, tick, and hand color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Primary text, tick, and hand color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    /// <summary>The "Show Date" property: display the calendar date badge.</summary>
    [WidgetProperty("Show Date", WidgetPropertyType.Boolean, "Display calendar date badge", true)]
    public bool ShowDate { get; set; } = true;

    /// <summary>Test seam: injectable clock for the rendered time.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    // Hoisted paints: the colors mutate per render (property-driven), so the
    // 30 FPS render allocates no SKPaint — not even the tick/hand paints.
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _amPaint = new() { IsAntialias = true };
    private readonly SKPaint _datePaint = new() { IsAntialias = true };
    private readonly SKPaint _facePaint = new() { IsAntialias = true };
    private readonly SKPaint _majorTickPaint = new() { StrokeWidth = 3f, IsAntialias = true };
    private readonly SKPaint _minorTickPaint = new() { StrokeWidth = 1.5f, IsAntialias = true };
    private readonly SKPaint _hourHandPaint = new() { StrokeWidth = 4.5f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    private readonly SKPaint _minuteHandPaint = new() { StrokeWidth = 3f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    private readonly SKPaint _secondHandPaint = new() { StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    private readonly SKPaint _centerDotPaint = new() { IsAntialias = true };

    // The digital-mode strings are memoized on the displayed second + date +
    // format: the time string changes once per second (the date once a day),
    // so the 30 FPS render reformats once per second, not once per frame.
    private long _memoSecond = -1;
    private DateTime _memoDay;
    private string _memoFormat = "";
    private string _memoTime = "";
    private string _memoAmpm = "";
    private string _memoDate = "";

    private (string Time, string Ampm, string Date) DigitalStrings(DateTime now)
    {
        long second = (long)now.TimeOfDay.TotalSeconds;
        if (_memoSecond == second && _memoDay == now.Date && string.Equals(_memoFormat, TimeFormat, StringComparison.Ordinal))
        {
            return (_memoTime, _memoAmpm, _memoDate);
        }

        _memoSecond = second;
        _memoDay = now.Date;
        _memoFormat = TimeFormat;
        _memoTime = ClockPresentation.FormatClockTime(now, TimeFormat);
        _memoAmpm = ClockPresentation.AmPm(now, TimeFormat);
        _memoDate = ClockPresentation.Date(now);
        return (_memoTime, _memoAmpm, _memoDate);
    }

    /// <summary>
    /// Draws the clock in the selected mode (digital or analog) for the
    /// injectable clock's current time.
    /// </summary>
    /// <param name="canvas">The frame canvas.</param>
    /// <param name="bounds">The widget's placement bounds.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var now = Clock.GetLocalNow().LocalDateTime;
        SKColor accentColor = ColorOf(AccentColorHex, WidgetPalette.Accent);
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);

        if (string.Equals(ClockMode, "Analog", StringComparison.Ordinal))
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
        var (timeStr, amPmStr, dateStr) = DigitalStrings(now);

        float fontSize = Math.Min(bounds.Width / 5.5f, bounds.Height / 2.2f);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontSize);
        _textPaint.Color = textColor;

        var timeBounds = new SKRect();
        font.MeasureText(timeStr, out timeBounds, _textPaint);

        float centerX = bounds.MidX;
        float centerY = ShowDate ? bounds.MidY - 10f : bounds.MidY + (timeBounds.Height / 3f);

        canvas.DrawTextWithFallback(timeStr, centerX - (timeBounds.Width / 2f), centerY, font, _textPaint);

        if (!string.IsNullOrEmpty(amPmStr))
        {
            var amFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontSize * 0.35f);
            _amPaint.Color = accentColor;
            canvas.DrawTextWithFallback(amPmStr, centerX + (timeBounds.Width / 2f) + 8f, centerY - (timeBounds.Height * 0.45f), amFont, _amPaint);
        }

        if (ShowDate)
        {
            var dateFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontSize * 0.32f);
            _datePaint.Color = textColor;
            var dateBounds = new SKRect();
            dateFont.MeasureText(dateStr, out dateBounds, _datePaint);
            canvas.DrawTextWithFallback(dateStr, centerX - (dateBounds.Width / 2f), centerY + (fontSize * 0.5f) + 10f, dateFont, _datePaint);
        }
    }

    private void RenderAnalog(SKCanvas canvas, SKRect bounds, DateTime now, SKColor accentColor, SKColor textColor)
    {
        float radius = Math.Min(bounds.Width, bounds.Height) / 2f - 15f;
        float cx = bounds.MidX;
        float cy = bounds.MidY;

        _facePaint.Color = textColor.WithAlpha(10);
        canvas.DrawCircle(cx, cy, radius, _facePaint);

        _majorTickPaint.Color = textColor;
        _minorTickPaint.Color = textColor;
        for (int i = 0; i < 12; i++)
        {
            float angle = i * 30f * (float)(Math.PI / 180f);
            float x1 = cx + (radius - 12f) * (float)Math.Sin(angle);
            float y1 = cy - (radius - 12f) * (float)Math.Cos(angle);
            float x2 = cx + radius * (float)Math.Sin(angle);
            float y2 = cy - radius * (float)Math.Cos(angle);
            canvas.DrawLine(x1, y1, x2, y2, i % 3 == 0 ? _majorTickPaint : _minorTickPaint);
        }

        (float hourAngle, float minAngle, float secAngle) = ClockPresentation.HandAngles(now);

        _hourHandPaint.Color = textColor;
        _minuteHandPaint.Color = textColor;
        _secondHandPaint.Color = accentColor;
        DrawHand(canvas, cx, cy, hourAngle, radius * 0.5f, _hourHandPaint);
        DrawHand(canvas, cx, cy, minAngle, radius * 0.75f, _minuteHandPaint);
        DrawHand(canvas, cx, cy, secAngle, radius * 0.85f, _secondHandPaint);

        _centerDotPaint.Color = textColor;
        canvas.DrawCircle(cx, cy, 5f, _centerDotPaint);
    }

    private static void DrawHand(SKCanvas canvas, float cx, float cy, float angleRad, float length, SKPaint paint)
    {
        float x = cx + length * (float)Math.Sin(angleRad);
        float y = cy - length * (float)Math.Cos(angleRad);
        canvas.DrawLine(cx, cy, x, y, paint);
    }

    /// <summary>Disposes the widget's hoisted Skia paints.</summary>
    public override ValueTask DisposeAsync()
    {
        _textPaint.Dispose();
        _amPaint.Dispose();
        _datePaint.Dispose();
        _facePaint.Dispose();
        _majorTickPaint.Dispose();
        _minorTickPaint.Dispose();
        _hourHandPaint.Dispose();
        _minuteHandPaint.Dispose();
        _secondHandPaint.Dispose();
        _centerDotPaint.Dispose();
        return base.DisposeAsync();
    }
}
