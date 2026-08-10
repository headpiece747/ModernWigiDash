using ModernWigiDash.Sdk;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("stopwatch_timer", "Stopwatch & Timer", Category = "Utilities")]
public class StopwatchTimerWidget : ModernWidgetBase
{
    public override SKSize DefaultSize => GridSizePreset.Size1x1.ToSize();

    private bool _isRunning = false;
    private DateTime _startTime;
    private TimeSpan _elapsed = TimeSpan.Zero;

    /// <summary>Test seam — the timing math is otherwise untestable.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Primed from <see cref="Clock"/> so a paused-at-zero stopwatch
    /// shows 0:00.00 regardless of when the widget was constructed.</summary>
    public StopwatchTimerWidget()
    {
        _startTime = Clock.GetUtcNow().UtcDateTime;
    }

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Timer digits color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Status label color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    private DateTime Now => Clock.GetUtcNow().UtcDateTime;

    /// <summary>Internal test accessor for the accumulated elapsed time.</summary>
    internal TimeSpan ElapsedForTest => _isRunning ? _elapsed + (Now - _startTime) : _elapsed;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var total = _isRunning ? _elapsed + (Now - _startTime) : _elapsed;
        string timeStr = StopwatchPresentation.FormatElapsed(total);
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        SKColor accentColor = ColorOf(AccentColorHex, SKColors.White);

        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, bounds.Width * 0.18f);
        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };
        var tb = new SKRect();
        font.MeasureText(timeStr, out tb, textPaint);
        canvas.DrawTextWithFallback(timeStr, bounds.MidX - (tb.Width / 2f), bounds.MidY - 5f, font, textPaint);

        var subFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f);
        using var subPaint = new SKPaint { Color = accentColor, IsAntialias = true };
        string statusStr = StopwatchPresentation.StatusText(_isRunning);
        var sb = new SKRect();
        subFont.MeasureText(statusStr, out sb, subPaint);
        float dotR = 4f;
        float dotX = bounds.MidX - (sb.Width / 2f) - dotR * 2f - 5f;
        float dotY = bounds.Bottom - 16f - 4f;
        using var dotPaint = new SKPaint { Color = StopwatchPresentation.StatusColor(_isRunning), IsAntialias = true };
        canvas.DrawCircle(dotX, dotY, dotR, dotPaint);
        canvas.DrawTextWithFallback(statusStr, bounds.MidX - (sb.Width / 2f), bounds.Bottom - 16f, subFont, subPaint);
    }

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
}
