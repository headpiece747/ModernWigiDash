namespace ModernWigiDash.Widgets;

/// <summary>
/// Encapsulates zero-allocation Skia rendering for digital and analog clocks.
/// Hoists all 10 <see cref="SKPaint"/> instances, memoizes formatted digital time/date strings,
/// and computes analog clock face, tick marks, and trigonometric hand positions.
/// </summary>
internal sealed class ClockCanvas : IDisposable
{
    // Hoisted paints: colors mutate per render (property-driven), so the
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
    // format + seconds toggle: the time string changes once per second (the
    // date once a day), so the 30 FPS render reformats once per second, not
    // once per frame, and a seconds toggle within the same second reformats
    // once instead of serving the stale string. The shared single-slot memo
    // owns the key-compare; the key is the four slow-changing inputs.
    private readonly MemoSlot<DigitalClockKey, DigitalClockStrings> _digitalMemo = new();

    /// <summary>The digital-string memo key: the four slow-changing format inputs.</summary>
    private readonly record struct DigitalClockKey(long Second, DateTime Day, string Format, bool ShowSeconds);

    /// <summary>The digital-string memo value: the three formatted strings.</summary>
    private readonly record struct DigitalClockStrings(string Time, string Ampm, string Date);

    private DigitalClockStrings DigitalStrings(DateTime now, string timeFormat, bool showSeconds)
    {
        var key = new DigitalClockKey((long)now.TimeOfDay.TotalSeconds, now.Date, timeFormat, showSeconds);
        if (_digitalMemo.TryGet(key, out var value))
        {
            return value;
        }

        value = new DigitalClockStrings(
            ClockPresentation.FormatClockTime(now, timeFormat, showSeconds),
            ClockPresentation.AmPm(now, timeFormat),
            ClockPresentation.Date(now));
        _digitalMemo.Set(key, value);
        return value;
    }

    /// <summary>
    /// Renders the digital clock display with time, optional AM/PM indicator, and date badge.
    /// </summary>
    public void RenderDigital(
        SKCanvas canvas,
        SKRect bounds,
        DateTime now,
        string timeFormat,
        bool showSeconds,
        bool showDate,
        SKColor accentColor,
        SKColor textColor)
    {
        var (timeStr, amPmStr, dateStr) = DigitalStrings(now, timeFormat, showSeconds);

        // The width divisor is the 5-char hh:mm/HH:mm shape (5.5); the seconds
        // variant is 8 chars and scales the divisor in proportion so the
        // longer string keeps the same per-char fit.
        int timeCharCount = 5 + (showSeconds ? 3 : 0);
        float fontSize = Math.Min(bounds.Width / (timeCharCount * 1.1f), bounds.Height / 2.2f);
        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontSize);
        _textPaint.Color = textColor;

        var timeBounds = new SKRect();
        font.MeasureText(timeStr, out timeBounds, _textPaint);

        float centerX = bounds.MidX;
        float centerY = showDate ? bounds.MidY - 10f : bounds.MidY + (timeBounds.Height / 3f);

        canvas.DrawTextWithFallback(timeStr, centerX - (timeBounds.Width / 2f), centerY, font, _textPaint);

        if (!string.IsNullOrEmpty(amPmStr))
        {
            var amFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fontSize * 0.35f);
            _amPaint.Color = accentColor;
            canvas.DrawTextWithFallback(amPmStr, centerX + (timeBounds.Width / 2f) + 8f, centerY - (timeBounds.Height * 0.45f), amFont, _amPaint);
        }

        if (showDate)
        {
            var dateFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontSize * 0.32f);
            _datePaint.Color = textColor;
            var dateBounds = new SKRect();
            dateFont.MeasureText(dateStr, out dateBounds, _datePaint);
            canvas.DrawTextWithFallback(dateStr, centerX - (dateBounds.Width / 2f), centerY + (fontSize * 0.5f) + 10f, dateFont, _datePaint);
        }
    }

    /// <summary>
    /// Renders the analog clock display with circular face, 12 tick marks, hour/minute/second hands, and center dot.
    /// </summary>
    public void RenderAnalog(
        SKCanvas canvas,
        SKRect bounds,
        DateTime now,
        SKColor accentColor,
        SKColor textColor)
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

    /// <summary>Disposes all hoisted Skia paints.</summary>
    public void Dispose()
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
    }
}
