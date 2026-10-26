namespace ModernWigiDash.Widgets;

/// <summary>
/// Clock widget: a digital mode (time, optional AM/PM, and the date badge)
/// and an analog mode (face, ticks, and hour/minute/second hands). The
/// display strings and the hand angles come from the ClockPresentation
/// module, rendered through <see cref="ClockCanvas"/>.
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

    /// <summary>The "Show Seconds" property: display seconds in digital mode
    /// (the hh:mm:ss / HH:mm:ss variants; the analog mode, the AM/PM badge,
    /// and the date are untouched).</summary>
    [WidgetProperty("Show Seconds", WidgetPropertyType.Boolean, "Display seconds in digital mode", false)]
    public bool ShowSeconds { get; set; } = false;

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

    private readonly ClockCanvas _canvas = new();

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
            _canvas.RenderAnalog(canvas, bounds, now, accentColor, textColor);
        }
        else
        {
            _canvas.RenderDigital(canvas, bounds, now, TimeFormat, ShowSeconds, ShowDate, accentColor, textColor);
        }
    }

    /// <summary>Disposes the widget's canvas and hoisted Skia paints.</summary>
    public override ValueTask DisposeAsync()
    {
        _canvas.Dispose();
        return base.DisposeAsync();
    }
}
