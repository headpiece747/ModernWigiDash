namespace ModernWigiDash.Widgets;

/// <summary>
/// Encapsulates zero-allocation Skia rendering for the stopwatch &amp; timer widget.
/// Hoists all 3 <see cref="SKPaint"/> instances and renders the formatted elapsed time,
/// status indicator dot, and status label. The elapsed string is memoized on the
/// displayed centisecond value (the house single-slot model), so a steady-state
/// tick (paused, or the same displayed value) reformats nothing.
/// </summary>
internal sealed class StopwatchTimerCanvas : IDisposable
{
    // Hoisted paints: colors mutate per render (the property values can change
    // via the inspector), so the 30 FPS render allocates no SKPaint.
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _subPaint = new() { IsAntialias = true };
    private readonly SKPaint _dotPaint = new() { IsAntialias = true };

    // The elapsed-string memo (the house's single-slot model): the displayed
    // "mm:ss.cc" value is a pure function of (minutes, seconds, centiseconds),
    // so the key is stable while the stopwatch is paused and changes at most
    // once per centisecond while it runs - the 30 FPS render reformats only
    // on a displayed change, never per frame.
    // The elapsed string changes at most once per centisecond; the shared
    // single-slot memo owns the key-compare (the house memo model).
    private readonly MemoSlot<int, string> _elapsedMemo = new();

    private string ElapsedString(TimeSpan total)
    {
        int cs = total.Minutes * 6000 + total.Seconds * 100 + total.Milliseconds / 10;
        if (_elapsedMemo.TryGet(cs, out var value))
        {
            return value;
        }

        value = StopwatchPresentation.FormatElapsed(total);
        _elapsedMemo.Set(cs, value);
        return value;
    }

    /// <summary>
    /// Draws the formatted elapsed time with its status dot and status line (running or paused).
    /// </summary>
    public void Render(SKCanvas canvas, SKRect bounds, TimeSpan total, bool isRunning, SKColor textColor, SKColor accentColor)
    {
        string timeStr = ElapsedString(total);

        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, bounds.Width * 0.18f);
        _textPaint.Color = textColor;
        var tb = new SKRect();
        font.MeasureText(timeStr, out tb, _textPaint);
        canvas.DrawTextWithFallback(timeStr, bounds.MidX - (tb.Width / 2f), bounds.MidY - 5f, font, _textPaint);

        var subFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f);
        _subPaint.Color = accentColor;
        string statusStr = StopwatchPresentation.StatusText(isRunning);
        var sb = new SKRect();
        subFont.MeasureText(statusStr, out sb, _subPaint);
        float dotR = 4f;
        float dotX = bounds.MidX - (sb.Width / 2f) - dotR * 2f - 5f;
        float dotY = bounds.Bottom - 16f - 4f;
        _dotPaint.Color = StopwatchPresentation.StatusColor(isRunning);
        canvas.DrawCircle(dotX, dotY, dotR, _dotPaint);
        canvas.DrawTextWithFallback(statusStr, bounds.MidX - (sb.Width / 2f), bounds.Bottom - 16f, subFont, _subPaint);
    }

    /// <summary>Disposes all hoisted Skia paints.</summary>
    public void Dispose()
    {
        _textPaint.Dispose();
        _subPaint.Dispose();
        _dotPaint.Dispose();
    }
}
