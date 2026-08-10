using System.Buffers;
using System.Text;
using ModernWigiDash.Core.Rendering;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Shared text-measurement and placeholder/sparkline-rendering helpers used by multiple widgets.
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

        // Binary-search the longest prefix that fits with the ellipsis —
        // the old linear probe allocated a substring + concat per step (O(n²)
        // on the 30 FPS path for long labels).
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
    /// Greedy word wrap: splits <paramref name="text"/> into lines that fit
    /// within <paramref name="maxWidth"/> measured with <paramref name="font"/>.
    /// Words are never split — a word wider than the available width gets its
    /// own line. An empty/null text yields a single empty line, and a
    /// <paramref name="maxWidth"/> ≤ 0 yields one word per line (matching the
    /// edge semantics of the two former per-widget copies).
    /// </summary>
    internal static List<string> WrapText(string text, SKFont font, float maxWidth)
    {
        List<string> result = [];
        if (string.IsNullOrEmpty(text))
        {
            result.Add("");
            return result;
        }

        if (FontHelper.MeasureTextWithFallback(text, font) <= maxWidth)
        {
            result.Add(text);
            return result;
        }

        var current = new StringBuilder();
        foreach (string word in text.Split(' '))
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (FontHelper.MeasureTextWithFallback(candidate, font) <= maxWidth)
            {
                current.Clear();
                current.Append(candidate);
            }
            else
            {
                if (current.Length > 0) result.Add(current.ToString());
                current.Clear();
                current.Append(word);
            }
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>
    /// Draws a centered title/subtitle placeholder (bold title above a dimmer subtitle).
    /// </summary>
    internal static void DrawTitleSubtitlePlaceholder(SKCanvas canvas, SKRect bounds, string title, string subtitle, SKColor text)
    {
        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 16f);
        using var titlePaint = new SKPaint { Color = text, IsAntialias = true };
        DrawCenteredText(canvas, title, bounds.MidX, bounds.MidY - 2f, titleFont, titlePaint);

        var subFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, 11f);
        using var subPaint = new SKPaint { Color = text.WithAlpha(150), IsAntialias = true };
        DrawCenteredText(canvas, subtitle, bounds.MidX, bounds.MidY + 20f, subFont, subPaint);
    }

    /// <summary>
    /// Draws a line-plus-fill sparkline for <paramref name="samples"/> normalized
    /// into the <paramref name="lo"/>..<paramref name="hi"/> range inside <paramref name="area"/>.
    /// Zero-allocation span overload for float histories (telemetry widgets).
    /// </summary>
    internal static void DrawSparkline(SKCanvas canvas, SKRect area, ReadOnlySpan<float> samples, float lo, float hi, SKColor accent)
    {
        BuildSparklinePaths(area, samples, lo, hi, out SKPath? line, out SKPath? fill);
        if (line == null || fill == null) return;

        using var fillPaint = new SKPaint { Color = accent.WithAlpha(40), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(fill, fillPaint);
        fill.Dispose();

        using var linePaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        canvas.DrawPath(line, linePaint);
        line.Dispose();
    }

    /// <summary>
    /// Draws a line-plus-fill sparkline for <paramref name="samples"/> normalized
    /// into the <paramref name="lo"/>..<paramref name="hi"/> range inside <paramref name="area"/>.
    /// </summary>
    internal static void DrawSparkline(SKCanvas canvas, SKRect area, IReadOnlyList<double> samples, double lo, double hi, SKColor accent)
    {
        BuildSparklinePaths(area, samples, lo, hi, out SKPath? line, out SKPath? fill);
        if (line == null || fill == null) return;

        using var fillPaint = new SKPaint { Color = accent.WithAlpha(40), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(fill, fillPaint);
        fill.Dispose();

        using var linePaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        canvas.DrawPath(line, linePaint);
        line.Dispose();
    }

    /// <summary>
    /// Builds the sparkline line + fill paths. Exposed so 30 FPS renderers can
    /// cache the paths and rebuild them only when the samples change.
    /// </summary>
    internal static void BuildSparklinePaths(SKRect area, IReadOnlyList<double> samples, double lo, double hi, out SKPath? line, out SKPath? fill)
    {
        line = null;
        fill = null;
        int count = samples.Count;
        if (count < 2) return;

        // The single implementation is span-based; convert the list once.
        if (count <= 256)
        {
            Span<float> floats = stackalloc float[count];
            for (int i = 0; i < count; i++)
            {
                floats[i] = (float)samples[i];
            }
            BuildSparklinePaths(area, floats, (float)lo, (float)hi, out line, out fill);
            return;
        }

        float[] rented = ArrayPool<float>.Shared.Rent(count);
        try
        {
            Span<float> floats = rented.AsSpan(0, count);
            for (int i = 0; i < count; i++)
            {
                floats[i] = (float)samples[i];
            }
            BuildSparklinePaths(area, floats, (float)lo, (float)hi, out line, out fill);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// The single sparkline path builder: builds the line + fill paths for
    /// <paramref name="samples"/> normalized into the <paramref name="lo"/>..<paramref name="hi"/>
    /// range inside <paramref name="area"/>. Zero-allocation span overload; the
    /// double-list entry points delegate here.
    /// </summary>
    internal static void BuildSparklinePaths(SKRect area, ReadOnlySpan<float> samples, float lo, float hi, out SKPath? line, out SKPath? fill)
    {
        line = null;
        fill = null;
        if (samples.Length < 2) return;

        // Guard against a degenerate zero-height band (lo == hi), which would
        // otherwise normalize to NaN coordinates.
        if (hi - lo == 0f)
        {
            lo -= 1f;
            hi += 1f;
        }

        float span = area.Width / Math.Max(1, samples.Length - 1);
        var lineBuilder = new SKPathBuilder();
        var fillBuilder = new SKPathBuilder();
        for (int i = 0; i < samples.Length; i++)
        {
            float x = area.Left + i * span;
            float y = area.Bottom - (samples[i] - lo) / (hi - lo) * area.Height;
            if (i == 0)
            {
                lineBuilder.MoveTo(x, y);
                fillBuilder.MoveTo(x, y);
            }
            else
            {
                lineBuilder.LineTo(x, y);
                fillBuilder.LineTo(x, y);
            }
        }

        fillBuilder.LineTo(area.Right, area.Bottom);
        fillBuilder.LineTo(area.Left, area.Bottom);
        fillBuilder.Close();

        fill = fillBuilder.Detach();
        line = lineBuilder.Detach();
    }
}
