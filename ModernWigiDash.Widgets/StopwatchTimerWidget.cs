using ModernWigiDash.Sdk;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("stopwatch_timer", "Stopwatch & Timer", Description = "Precision millisecond stopwatch with touch Start/Pause/Reset controls.", Author = "ModernWigiDash", Version = "2.0.0", Category = "Utilities", DefaultGridSize = GridSizePreset.Size1x1)]
public class StopwatchTimerWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size1x1.ToSize();

    private bool _isRunning = false;
    private DateTime _startTime = TimeProvider.System.GetUtcNow().UtcDateTime;
    private TimeSpan _elapsed = TimeSpan.Zero;

    /// <summary>Test seam — the timing math is otherwise untestable.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

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
        string timeStr = $"{total.Minutes:D2}:{total.Seconds:D2}.{total.Milliseconds / 10:D2}";
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        SKColor accentColor = ColorOf(AccentColorHex, SKColors.White);

        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, bounds.Width * 0.18f);
        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };
        var tb = new SKRect();
        font.MeasureText(timeStr, out tb, textPaint);
        canvas.DrawTextWithFallback(timeStr, bounds.MidX - (tb.Width / 2f), bounds.MidY - 5f, font, textPaint);

        var subFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f);
        using var subPaint = new SKPaint { Color = accentColor, IsAntialias = true };
        string statusStr = _isRunning ? "TAP TO PAUSE" : "TAP TO START";
        var sb = new SKRect();
        subFont.MeasureText(statusStr, out sb, subPaint);
        float dotR = 4f;
        float dotX = bounds.MidX - (sb.Width / 2f) - dotR * 2f - 5f;
        float dotY = bounds.Bottom - 16f - 4f;
        using var dotPaint = new SKPaint { Color = _isRunning ? new SKColor(239, 68, 68) : new SKColor(34, 197, 94), IsAntialias = true };
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
