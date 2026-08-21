namespace ModernWigiDash.Widgets;

/// <summary>
/// Shared text-measurement and placeholder-rendering helpers used by multiple
/// widgets. The sparkline surface lives in the <c>SparklineRenderer</c>
/// module and greedy word-wrap is private to <c>WrapCache</c>, the wrap
/// result's only consumer — each rule here has at least two production
/// consumers.
/// </summary>
internal static class TextRenderHelper
{
    /// <summary>
    /// Truncates <paramref name="text"/> to fit within <paramref name="maxWidth"/> using
    /// <paramref name="font"/>, appending an ellipsis when trimming is required.
    /// </summary>
    internal static string TruncateText(string text, SKFont font, float maxWidth)
    {
        return TruncateTextCore(text, font, null, maxWidth);
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to fit within <paramref name="maxWidth"/>,
    /// measuring with <paramref name="paint"/> (used when paint-aware metrics are
    /// required). Appends an ellipsis when trimming is required.
    /// </summary>
    internal static string TruncateText(string text, SKFont font, SKPaint paint, float maxWidth)
    {
        return TruncateTextCore(text, font, paint, maxWidth);
    }

    /// <summary>
    /// Shared truncation loop. A null <paramref name="paint"/> measures with font
    /// metrics only; a non-null one measures paint-aware (typeface/effects),
    /// preserving each public overload's measurement semantics.
    /// </summary>
    private static string TruncateTextCore(string text, SKFont font, SKPaint? paint, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || Measure(text) <= maxWidth)
            return text;

        string ellipsis = "…";
        float ellipsisW = Measure(ellipsis);
        if (ellipsisW >= maxWidth) return "";

        // Binary-search the longest prefix that fits with the ellipsis: a
        // linear probe is O(n²) substring work on the 30 FPS path for long labels.
        int lo = 0;
        int hi = text.Length - 1;
        int best = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Measure(text[..(mid + 1)] + ellipsis) <= maxWidth)
            {
                best = mid + 1;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return best > 0 ? text[..best] + ellipsis : ellipsis;

        float Measure(string value) => paint is null ? font.MeasureText(value) : font.MeasureText(value, paint);
    }

    /// <summary>
    /// Draws <paramref name="text"/> horizontally centered around <paramref name="centerX"/>,
    /// with its baseline at <paramref name="baselineY"/>.
    /// </summary>
    internal static void DrawCenteredText(SKCanvas canvas, string text, float centerX, float baselineY, SKFont font, SKPaint paint)
    {
        float width = FontHelper.MeasureTextWithFallback(text, font);
        canvas.DrawTextWithFallback(text, centerX - width / 2f, baselineY, font, paint);
    }

    /// <summary>
    /// Draws a centered title/subtitle placeholder (bold title above a dimmer
    /// subtitle). The caller supplies its own hoisted paints — the placeholder
    /// is the per-frame state of the unavailable widgets, so the paints must
    /// not be allocated here.
    /// </summary>
    internal static void DrawTitleSubtitlePlaceholder(SKCanvas canvas, SKRect bounds, string title, string subtitle, SKColor text, SKPaint titlePaint, SKPaint subPaint)
    {
        titlePaint.Color = text;
        DrawCenteredText(canvas, title, bounds.MidX, bounds.MidY - 2f, FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 16f), titlePaint);

        subPaint.Color = text.WithAlpha(150);
        DrawCenteredText(canvas, subtitle, bounds.MidX, bounds.MidY + 20f, FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 11f), subPaint);
    }
}
