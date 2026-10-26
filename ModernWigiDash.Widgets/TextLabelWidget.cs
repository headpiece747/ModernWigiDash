namespace ModernWigiDash.Widgets;

/// <summary>
/// Multi-line text label: word-wrapped text at the chosen font, size, color,
/// and alignment, optionally on a rounded-rectangle background. The wrap and
/// the fit-to-bounds results are memoized, so a static scene allocates
/// nothing per frame.
/// </summary>
[WidgetMetadata("text_label", "Text", Category = "Utilities", DefaultGridSize = GridSizePreset.Size2x1)]
public class TextLabelWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider
{
    /// <summary>The "Text" property: the text to display (supports multiple lines).</summary>
    [WidgetProperty("Text", WidgetPropertyType.Text, "Text to display (supports multiple lines)", "Your text here")]
    public string Text { get; set; } = "Your text here";

    /// <summary>The "Font Family" property: the system font used to render the text.</summary>
    [WidgetProperty("Font Family", WidgetPropertyType.Font, "System font used to render the text", "Geist")]
    public string FontFamily { get; set; } = "Geist";

    /// <summary>The "Font Size" property: the text size in points.</summary>
    [WidgetProperty("Font Size", WidgetPropertyType.Number, "Text size in points", 32)]
    public int FontSize { get; set; } = 32;

    /// <summary>The "Text Color" property: the text color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Text color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    /// <summary>The "Alignment" property: horizontal text alignment (Left, Center, Right).</summary>
    [WidgetProperty("Alignment", WidgetPropertyType.Choice, "Horizontal text alignment", "Center", "Left", "Center", "Right")]
    public string Alignment { get; set; } = "Center";

    /// <summary>The "Background Color" property: the rounded-rectangle background (transparent disables it).</summary>
    [WidgetProperty("Background Color", WidgetPropertyType.Color, "Rounded-rectangle background (use transparent to disable)", "#00000000")]
    public string BackgroundHex { get; set; } = "#00000000";

    /// <summary>
    /// The inspector's dynamic choice list: the available system font
    /// families for the Font Family property.
    /// </summary>
    /// <param name="propertyName">The property the inspector is editing.</param>
    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (!string.Equals(propertyName, nameof(FontFamily), StringComparison.Ordinal)) return [];
        return FontHelper.GetAllFamilies()
            .Select(family => new WidgetPropertyOption(family, family))
            .ToArray();
    }

    /// <summary>
    /// Draws the background (when opaque) and the wrapped text, fitted to the
    /// bounds with an ellipsis on the last visible line when cut.
    /// </summary>
    /// <param name="canvas">The frame canvas.</param>
    /// <param name="bounds">The widget's placement bounds.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        _canvas.Render(canvas, bounds, Text, FontFamily, FontSize, TextColorHex, Alignment, BackgroundHex);
    }

    // The draw owner: hoisted paints, the wrap cache, the fit-lines memo,
    // and the pure fit-lines rule itself (the widget's draw paths and their
    // disposal live in one place).
    private readonly TextLabelCanvas _canvas = new();
    private bool _disposed;

    /// <summary>Disposes the widget's draw owner (its hoisted Skia paints).</summary>
    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _canvas.Dispose();
        return base.DisposeAsync();
    }
}
