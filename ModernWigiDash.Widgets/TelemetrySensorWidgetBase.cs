namespace ModernWigiDash.Widgets;

using SkiaSharp;
using ModernWigiDash.Sdk;

/// <summary>
/// Deep base class for telemetry sensor widgets (HardwareMonitorWidget, HwinfoWidget).
/// Encapsulates the shared property declarations, color parsing, sample history,
/// placeholder rendering, and TelemetryReadingRenderer lifecycle.
/// </summary>
public abstract class TelemetrySensorWidgetBase : ModernWidgetBase
{
    /// <summary>The "Display Label": overrides the label shown on the widget (empty = the sensor's name).</summary>
    [WidgetProperty("Display Label", WidgetPropertyType.Text, "Override the label shown on the widget (leave empty to use the sensor name)", "")]
    public string DisplayLabel { get; set; } = "";

    /// <summary>The "Unit": overrides the unit shown (empty = the sensor's unit).</summary>
    [WidgetProperty("Unit", WidgetPropertyType.Text, "Override the unit (leave empty to use the sensor's)", "")]
    public string Unit { get; set; } = "";

    /// <summary>The "Display Mode": how the reading is visualized (Gauge, Bar, Value, or Graph).</summary>
    [WidgetProperty("Display Mode", WidgetPropertyType.Choice, "How to visualize the reading", "Gauge", "Gauge", "Bar", "Value", "Graph")]
    public string DisplayMode { get; set; } = "Gauge";

    /// <summary>The "Auto Scale" toggle: scale the gauge/bar to the maximum recorded by the sensor.</summary>
    [WidgetProperty("Auto Scale", WidgetPropertyType.Boolean, "Scale the gauge/bar to the maximum recorded by the sensor", true)]
    public bool AutoScale { get; set; } = true;

    /// <summary>The "Max Value": the manual gauge/bar maximum when Auto Scale is off.</summary>
    [WidgetProperty("Max Value", WidgetPropertyType.Number, "Manual gauge/bar maximum when Auto Scale is off", 100f)]
    public float MaxValue { get; set; } = 100f;

    /// <summary>The "Decimals": the number of decimal places the hero value shows.</summary>
    [WidgetProperty("Decimals", WidgetPropertyType.Number, "Number of decimal places shown", 1f)]
    public float Decimals { get; set; } = 1f;

    /// <summary>The "Accent Color": the primary accent color (gauge progress, graph).</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Text Color": the header, label, and value color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Header, label, and value color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    /// <summary>The shared reading renderer instance.</summary>
    internal readonly TelemetryReadingRenderer Renderer = new();

    private bool _disposed;

    /// <summary>Internal test accessor: how many history samples are buffered.</summary>
    internal int HistoryCountForTest => Renderer.HistoryCountForTest;

    /// <summary>Draws the placeholder display with title, subtitle, and text color.</summary>
    protected void DrawPlaceholder(SKCanvas canvas, SKRect bounds, (string Title, string Sub) placeholder, SKColor text)
    {
        Renderer.DrawPlaceholder(canvas, bounds, placeholder.Title, placeholder.Sub, text);
    }

    /// <summary>Draws the placeholder display with explicit title and subtitle.</summary>
    protected void DrawPlaceholder(SKCanvas canvas, SKRect bounds, string title, string sub, SKColor text)
    {
        Renderer.DrawPlaceholder(canvas, bounds, title, sub, text);
    }

    /// <summary>Resolves the configured accent and text SKColors from hex strings with safe fallbacks.</summary>
    protected (SKColor Accent, SKColor Text) ResolveColors()
    {
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);
        SKColor text = ColorOf(TextColorHex, SKColors.White);
        return (accent, text);
    }

    /// <summary>
    /// The host's one teardown channel (the rehydrator's DisposeInstance):
    /// releases the renderer's native paints. The renderer's synchronous
    /// Dispose is spelled here, not behind an IDisposable face nothing in
    /// the tree called.
    /// </summary>
    public override ValueTask DisposeAsync()
    {
        if (_disposed)
            return base.DisposeAsync();
        _disposed = true;
        Renderer.Dispose();
        return base.DisposeAsync();
    }
}
