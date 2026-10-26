namespace ModernWigiDash.Widgets;

/// <summary>
/// The text label widget's draw owner: the hoisted paints, the wrap cache,
/// the fit-lines memo, and the pure fit-lines rule itself (the rule and its
/// memo live in one place, so the widget's draw paths and their disposal
/// live in one place too, the one-draw-owner rule in CONTEXT.md).
/// </summary>
internal sealed class TextLabelCanvas : IDisposable
{
    private readonly WrapCache _wrappedLines = new();

    // Hoisted paints (the 30 FPS render allocates no SKPaint).
    private readonly SKPaint _bgPaint = new() { IsAntialias = true };
    private readonly SKPaint _textPaint = new() { IsAntialias = true };

    // The fit-lines memo fields (see GetFittedLines).
    private IReadOnlyList<string>? _fitMemoWrapped;
    private SKFont? _fitMemoFont;
    private float _fitMemoMaxWidth;
    private float _fitMemoLineHeight;
    private float _fitMemoAvailableHeight;
    private IReadOnlyList<string>? _fitMemoDisplay;

    /// <summary>
    /// Draws the background (when opaque) and the wrapped text, fitted to the
    /// bounds with an ellipsis on the last visible line when cut.
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        string text,
        string fontFamily,
        int fontSize,
        string textColorHex,
        string alignment,
        string backgroundHex)
    {
        SKColor textColor = SkColorCache.ColorOf(textColorHex, SKColors.White);
        SKColor bgColor = SkColorCache.ColorOf(backgroundHex, SKColors.Transparent);

        if (bgColor.Alpha > 0)
        {
            _bgPaint.Color = bgColor;
            canvas.DrawRoundRect(bounds, 12f, 12f, _bgPaint);
        }

        var textAlignment = alignment switch
        {
            "Left" => SKTextAlign.Left,
            "Right" => SKTextAlign.Right,
            _ => SKTextAlign.Center
        };

        float fittedFontSize = Math.Max(6f, Math.Min(fontSize, bounds.Height / 2f));
        var font = FontHelper.GetCachedFont(FontHelper.GetTypeface(fontFamily, SKFontStyle.Normal), fittedFontSize);
        _textPaint.Color = textColor;

        float padding = Math.Min(12f, bounds.Width * 0.04f);
        float textWidth = bounds.Width - padding * 2f;

        // The wrapped line list is memoized per (Text, fontSize, width) — the
        // wrap loop measures every candidate, so per-frame recomputation is
        // wasted work at 30 FPS.
        IReadOnlyList<string> wrapped = _wrappedLines.GetOrWrap(text, font, fittedFontSize, textWidth);
        if (wrapped.Count == 0) return;

        float lineHeight = fittedFontSize * 1.25f;

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
        float firstBaseline = bounds.MidY - totalHeight / 2f + fittedFontSize * 0.8f;

        float anchorX = textAlignment switch
        {
            SKTextAlign.Left => bounds.Left + padding,
            SKTextAlign.Right => bounds.Right - padding,
            _ => bounds.MidX
        };

        for (int i = 0; i < display.Count; i++)
        {
            canvas.DrawTextWithFallback(display[i], anchorX, firstBaseline + i * lineHeight, font, _textPaint, textAlignment);
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
    /// callers draw it, they never mutate the wrapped cache. The rule sits
    /// beside its memo below, so the rule and the state that caches it have
    /// one owner.
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

    /// <summary>Disposes the widget's hoisted Skia paints.</summary>
    public void Dispose()
    {
        _bgPaint.Dispose();
        _textPaint.Dispose();
    }
}
