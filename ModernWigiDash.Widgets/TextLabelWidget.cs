
namespace ModernWigiDash.Widgets;

[WidgetMetadata("text_label", "Text", Category = "Utilities", DefaultGridSize = GridSizePreset.Size2x1)]
public class TextLabelWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider
{
    [WidgetProperty("Text", WidgetPropertyType.Text, "Text to display (supports multiple lines)", "Your text here")]
    public string Text { get; set; } = "Your text here";

    [WidgetProperty("Font Family", WidgetPropertyType.Font, "System font used to render the text", "Geist")]
    public string FontFamily { get; set; } = "Geist";

    [WidgetProperty("Font Size", WidgetPropertyType.Number, "Text size in points", 32)]
    public int FontSize { get; set; } = 32;

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Text color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Alignment", WidgetPropertyType.Choice, "Horizontal text alignment", "Center", "Left", "Center", "Right")]
    public string Alignment { get; set; } = "Center";

    [WidgetProperty("Background Color", WidgetPropertyType.Color, "Rounded-rectangle background (use transparent to disable)", "#00000000")]
    public string BackgroundHex { get; set; } = "#00000000";

    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (!string.Equals(propertyName, nameof(FontFamily), StringComparison.Ordinal)) return [];
        return FontHelper.GetAllFamilies()
            .Select(family => new WidgetPropertyOption(family, family))
            .ToArray();
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        SKColor bgColor = ColorOf(BackgroundHex, SKColors.Transparent);

        if (bgColor.Alpha > 0)
        {
            _bgPaint.Color = bgColor;
            canvas.DrawRoundRect(bounds, 12f, 12f, _bgPaint);
        }

        var alignment = Alignment switch
        {
            "Left" => SKTextAlign.Left,
            "Right" => SKTextAlign.Right,
            _ => SKTextAlign.Center
        };

        float fontSize = Math.Max(6f, Math.Min(FontSize, bounds.Height / 2f));
        var font = FontHelper.GetCachedFont(FontHelper.GetTypeface(FontFamily, SKFontStyle.Normal), fontSize);
        _textPaint.Color = textColor;

        float padding = Math.Min(12f, bounds.Width * 0.04f);
        float textWidth = bounds.Width - padding * 2f;

        // The wrapped line list is memoized per (Text, fontSize, width) — the
        // wrap loop measures every candidate, so per-frame recomputation is
        // wasted work at 30 FPS.
        IReadOnlyList<string> wrapped = _wrappedLines.GetOrWrap(Text, font, fontSize, textWidth);
        if (wrapped.Count == 0) return;

        float lineHeight = fontSize * 1.25f;

        // Fit the wrapped lines inside the bounds: cap the drawn line count to
        // what the height fits (ellipsis on the last visible line when cut),
        // and truncate any single line wider than the text width (a word wider
        // than the widget wraps onto its own line by design and would spill).
        // The fit result is single-slot memoized on the wrapped reference +
        // geometry — a static scene recomputes it never, not 30×/s.
        float availableHeight = bounds.Height - padding * 2f;
        IReadOnlyList<string> display = GetFittedLines(wrapped, font, textWidth, lineHeight, availableHeight);
        if (display.Count == 0) return;

        float totalHeight = display.Count * lineHeight;
        float firstBaseline = bounds.MidY - totalHeight / 2f + fontSize * 0.8f;

        float anchorX = alignment switch
        {
            SKTextAlign.Left => bounds.Left + padding,
            SKTextAlign.Right => bounds.Right - padding,
            _ => bounds.MidX
        };

        for (int i = 0; i < display.Count; i++)
        {
            canvas.DrawTextWithFallback(display[i], anchorX, firstBaseline + i * lineHeight, font, _textPaint, alignment);
        }
    }

    /// <summary>
    /// The fit-lines rule's single-slot memo: a static scene (same wrapped
    /// reference — which itself only changes when the text/size/width key
    /// turns — and same geometry) reuses the fitted list, so the per-frame
    /// path allocates nothing.
    /// </summary>
    private IReadOnlyList<string> GetFittedLines(IReadOnlyList<string> wrapped, SKFont font, float maxWidth, float lineHeight, float availableHeight)
    {
        // The float keys compare by bits (the house's SingleToInt32Bits
        // pattern): the memo key is an identity, not a measurement — any
        // change to the geometry rebuilds, a bit-identical geometry reuses.
        if (_fitMemoDisplay is not null && ReferenceEquals(wrapped, _fitMemoWrapped) && ReferenceEquals(font, _fitMemoFont)
            && BitConverter.SingleToInt32Bits(_fitMemoMaxWidth) == BitConverter.SingleToInt32Bits(maxWidth)
            && BitConverter.SingleToInt32Bits(_fitMemoLineHeight) == BitConverter.SingleToInt32Bits(lineHeight)
            && BitConverter.SingleToInt32Bits(_fitMemoAvailableHeight) == BitConverter.SingleToInt32Bits(availableHeight))
        {
            return _fitMemoDisplay;
        }

        _fitMemoWrapped = wrapped;
        _fitMemoFont = font;
        _fitMemoMaxWidth = maxWidth;
        _fitMemoLineHeight = lineHeight;
        _fitMemoAvailableHeight = availableHeight;
        _fitMemoDisplay = FitLinesToBounds(wrapped, font, maxWidth, lineHeight, availableHeight);
        return _fitMemoDisplay;
    }

    /// <summary>
    /// Pure display rule: caps <paramref name="wrapped"/> to the lines that fit
    /// within <paramref name="availableHeight"/> at <paramref name="lineHeight"/>
    /// (an ellipsis marks the last visible line when lines are cut), and
    /// truncates any single line wider than <paramref name="maxWidth"/> so
    /// text never spills past the widget bounds. The returned list is new —
    /// callers draw it, they never mutate the wrapped cache.
    /// </summary>
    internal static IReadOnlyList<string> FitLinesToBounds(
        IReadOnlyList<string> wrapped, SKFont font, float maxWidth, float lineHeight, float availableHeight)
    {
        int maxLines = Math.Max(1, (int)(availableHeight / lineHeight));
        bool truncated = wrapped.Count > maxLines;
        int count = Math.Min(wrapped.Count, maxLines);
        if (count == 0) return [];

        List<string> display = new(count);
        for (int i = 0; i < count; i++)
        {
            // TruncateText is a no-op for lines that already fit; it truncates
            // an over-wide word (its own line by WrapText's design).
            display.Add(TextRenderHelper.TruncateText(wrapped[i], font, maxWidth));
        }

        if (truncated)
        {
            // Signal the cut with an ellipsis on the last visible line (the
            // appended " …" is itself truncated if the line is full).
            display[^1] = TextRenderHelper.TruncateText(display[^1] + " …", font, maxWidth);
        }
        return display;
    }

    private readonly WrapCache _wrappedLines = new();

    // Hoisted paints (the 30 FPS render allocates no SKPaint).
    private readonly SKPaint _bgPaint = new() { IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };
    private bool _disposed;

    // The fit-lines memo fields (see GetFittedLines).
    private IReadOnlyList<string>? _fitMemoWrapped;
    private SKFont? _fitMemoFont;
    private float _fitMemoMaxWidth;
    private float _fitMemoLineHeight;
    private float _fitMemoAvailableHeight;
    private IReadOnlyList<string>? _fitMemoDisplay;

    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _bgPaint.Dispose();
        _textPaint.Dispose();
        return base.DisposeAsync();
    }
}
