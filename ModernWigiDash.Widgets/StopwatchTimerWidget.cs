namespace ModernWigiDash.Widgets;

/// <summary>
/// The Stopwatch &amp; Timer widget: a tap toggles the stopwatch and the
/// render draws the elapsed time with its running/paused status line.
/// </summary>
[WidgetMetadata("stopwatch_timer", "Stopwatch & Timer", Category = "Utilities", DefaultGridSize = GridSizePreset.Size1x1)]
public class StopwatchTimerWidget : ModernWidgetBase
{
    private bool _isRunning = false;
    private DateTime _startTime;
    private TimeSpan _elapsed = TimeSpan.Zero;

    /// <summary>Test seam — the timing math is otherwise untestable.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    // Hoisted paints: colors mutate per render (the property values can change
    // via the inspector), so the 30 FPS render allocates no SKPaint.
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private readonly SKPaint _subPaint = new() { IsAntialias = true };
    private readonly SKPaint _dotPaint = new() { IsAntialias = true };

    /// <summary>Primed from <see cref="Clock"/> so a paused-at-zero stopwatch
    /// shows 0:00.00 regardless of when the widget was constructed.</summary>
    public StopwatchTimerWidget()
    {
        _startTime = Clock.GetUtcNow().UtcDateTime;
    }

    /// <summary>The "Text Color": the timer digits color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Timer digits color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    /// <summary>The "Accent Color": the status label color.</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Status label color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    private DateTime Now => Clock.GetUtcNow().UtcDateTime;

    /// <summary>Internal test accessor for the accumulated elapsed time.</summary>
    internal TimeSpan ElapsedForTest => _isRunning ? _elapsed + (Now - _startTime) : _elapsed;

    /// <summary>
    /// Draws the elapsed time (StopwatchPresentation format) with its status
    /// dot and status line (running or paused).
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="bounds">The widget's bounds in canvas coordinates.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var total = _isRunning ? _elapsed + (Now - _startTime) : _elapsed;
        string timeStr = StopwatchPresentation.FormatElapsed(total);
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        SKColor accentColor = ColorOf(AccentColorHex, SKColors.White);

        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, bounds.Width * 0.18f);
        _textPaint.Color = textColor;
        var tb = new SKRect();
        font.MeasureText(timeStr, out tb, _textPaint);
        canvas.DrawTextWithFallback(timeStr, bounds.MidX - (tb.Width / 2f), bounds.MidY - 5f, font, _textPaint);

        var subFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f);
        _subPaint.Color = accentColor;
        string statusStr = StopwatchPresentation.StatusText(_isRunning);
        var sb = new SKRect();
        subFont.MeasureText(statusStr, out sb, _subPaint);
        float dotR = 4f;
        float dotX = bounds.MidX - (sb.Width / 2f) - dotR * 2f - 5f;
        float dotY = bounds.Bottom - 16f - 4f;
        _dotPaint.Color = StopwatchPresentation.StatusColor(_isRunning);
        canvas.DrawCircle(dotX, dotY, dotR, _dotPaint);
        canvas.DrawTextWithFallback(statusStr, bounds.MidX - (sb.Width / 2f), bounds.Bottom - 16f, subFont, _subPaint);
    }

    /// <summary>
    /// A TouchDown toggles the stopwatch (a pause accumulates the elapsed
    /// time, a resume restarts the start stamp).
    /// </summary>
    /// <param name="localPoint">The touch point in the widget's rotated-local coordinates.</param>
    /// <param name="eventType">The touch event type.</param>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchDown)
        {
            if (_isRunning)
            {
                _elapsed += Now - _startTime;
                _isRunning = false;
            }
            else
            {
                _startTime = Now;
                _isRunning = true;
            }
            Context?.RequestRender();
        }
    }

    /// <summary>Disposes the hoisted paints.</summary>
    public override ValueTask DisposeAsync()
    {
        _textPaint.Dispose();
        _subPaint.Dispose();
        _dotPaint.Dispose();
        return base.DisposeAsync();
    }
}
