namespace ModernWigiDash.Widgets;

/// <summary>
/// The Stopwatch &amp; Timer widget: a tap toggles the stopwatch and the
/// render draws the elapsed time with its running/paused status line via <see cref="StopwatchTimerCanvas"/>.
/// </summary>
[WidgetMetadata("stopwatch_timer", "Stopwatch & Timer", Category = "Utilities", DefaultGridSize = GridSizePreset.Size1x1)]
public class StopwatchTimerWidget : ModernWidgetBase
{
    private bool _isRunning = false;
    private DateTime _startTime;
    private TimeSpan _elapsed = TimeSpan.Zero;
    private readonly StopwatchTimerCanvas _canvas = new();

    /// <summary>Test seam — the timing math is otherwise untestable.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

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
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        SKColor accentColor = ColorOf(AccentColorHex, SKColors.White);

        _canvas.Render(canvas, bounds, total, _isRunning, textColor, accentColor);
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

    /// <summary>Disposes the canvas and hoisted paints.</summary>
    public override ValueTask DisposeAsync()
    {
        _canvas.Dispose();
        return base.DisposeAsync();
    }
}
