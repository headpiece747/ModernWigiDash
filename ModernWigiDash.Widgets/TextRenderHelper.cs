using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Shared text-measurement helpers used by multiple widgets.
/// </summary>
internal static class TextRenderHelper
{
    /// <summary>
    /// Truncates <paramref name="text"/> to fit within <paramref name="maxWidth"/> using
    /// <paramref name="font"/>, appending an ellipsis when trimming is required.
    /// </summary>
    internal static string TruncateText(string text, SKFont font, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || font.MeasureText(text) <= maxWidth)
            return text;

        string ellipsis = "…";
        float ellipsisW = font.MeasureText(ellipsis);
        if (ellipsisW >= maxWidth) return "";

        int len = text.Length;
        while (len > 0)
        {
            string sub = text[..len] + ellipsis;
            if (font.MeasureText(sub) <= maxWidth)
                return sub;
            len--;
        }
        return ellipsis;
    }
}
