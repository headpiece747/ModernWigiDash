using System.Collections.Concurrent;
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

    /// <summary>
    /// Truncates <paramref name="text"/> to fit within <paramref name="maxWidth"/>,
    /// measuring with <paramref name="paint"/> (used when paint-aware metrics are
    /// required). Appends an ellipsis when trimming is required.
    /// </summary>
    internal static string TruncateText(string text, SKFont font, SKPaint paint, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || font.MeasureText(text, paint) <= maxWidth)
            return text;

        string ellipsis = "…";
        float ellipsisW = font.MeasureText(ellipsis, paint);
        if (ellipsisW >= maxWidth) return "";

        int len = text.Length;
        while (len > 0)
        {
            string sub = text[..len] + ellipsis;
            if (font.MeasureText(sub, paint) <= maxWidth)
                return sub;
            len--;
        }
        return ellipsis;
    }

    /// <summary>
    /// Draws <paramref name="text"/> horizontally centered around <paramref name="centerX"/>,
    /// with its baseline at <paramref name="baselineY"/>.
    /// </summary>
    internal static void DrawCenteredText(SKCanvas canvas, string text, float centerX, float baselineY, SKFont font, SKPaint paint)
    {
        float width = font.MeasureText(text, paint);
        canvas.DrawText(text, centerX - width / 2f, baselineY, SKTextAlign.Left, font, paint);
    }

    /// <summary>
    /// Draws a centered title/subtitle placeholder (bold title above a dimmer subtitle).
    /// </summary>
    internal static void DrawTitleSubtitlePlaceholder(SKCanvas canvas, SKRect bounds, string title, string subtitle, SKColor text)
    {
        using var titleFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Bold), 16f);
        FontHelper.ConfigureHighQualityFont(titleFont);
        using var titlePaint = new SKPaint { Color = text, IsAntialias = true };
        DrawCenteredText(canvas, title, bounds.MidX, bounds.MidY - 2f, titleFont, titlePaint);

        using var subFont = new SKFont(FontHelper.GetTypeface("Geist", SKFontStyle.Normal), 11f);
        FontHelper.ConfigureHighQualityFont(subFont);
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
        if (samples.Length < 2) return;

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

        using var fillPaint = new SKPaint { Color = accent.WithAlpha(40), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(fillBuilder.Detach(), fillPaint);

        using var linePaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        canvas.DrawPath(lineBuilder.Detach(), linePaint);
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
        if (samples.Count < 2) return;

        float span = area.Width / Math.Max(1, samples.Count - 1);
        var lineBuilder = new SKPathBuilder();
        var fillBuilder = new SKPathBuilder();
        for (int i = 0; i < samples.Count; i++)
        {
            float x = area.Left + i * span;
            float y = area.Bottom - (float)((samples[i] - lo) / (hi - lo)) * area.Height;
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

    /// <summary>
    /// Draws <paramref name="path"/> centered at <paramref name="center"/> scaled to
    /// <paramref name="sizePx"/> by its largest bounds dimension, offset by
    /// <paramref name="offsetX"/>/<paramref name="offsetY"/>. Single shared scaling
    /// protocol for SVG-path icons.
    /// </summary>
    internal static void DrawPathScaled(SKCanvas canvas, SKPath path, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)
    {
        if (sizePx <= 0 || path.IsEmpty) return;
        var bounds = path.Bounds;
        float maxDim = Math.Max(bounds.Width, bounds.Height);
        if (maxDim <= 0) return;

        float scale = sizePx / maxDim;
        canvas.Save();
        canvas.Translate(center.X + offsetX, center.Y + offsetY);
        canvas.Scale(scale, scale);
        canvas.Translate(-bounds.MidX, -bounds.MidY);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawPath(path, paint);
        canvas.Restore();
    }

    /// <summary>
    /// Shared parsed-SVG-path cache with an empty-path fallback. Keyed
    /// case-insensitively; callers supply the raw path data per key.
    /// </summary>
    internal static class SvgPathCache
    {
        private static readonly ConcurrentDictionary<string, SKPath> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the cached parsed path for <paramref name="key"/>, parsing
        /// the path data produced by <paramref name="getPathData"/> on first
        /// use. Invalid or empty paths resolve to an empty <see cref="SKPath"/>
        /// (never null).
        /// </summary>
        internal static SKPath GetOrParse(string key, string pathData)
            => GetOrParse(key, () => pathData);

        /// <summary>
        /// Returns the cached parsed path for <paramref name="key"/>, parsing
        /// the path data produced by <paramref name="getPathData"/> on first
        /// use. Invalid or empty paths resolve to an empty <see cref="SKPath"/>
        /// (never null).
        /// </summary>
        internal static SKPath GetOrParse(string key, Func<string> getPathData)
        {
            return Cache.GetOrAdd(key, _ =>
            {
                try
                {
                    string pathData = getPathData();
                    if (string.IsNullOrWhiteSpace(pathData))
                        return new SKPath();

                    SKPath? parsed = SKPath.ParseSvgPathData(pathData);
                    if (parsed != null && parsed.Bounds.Width > 0 && parsed.Bounds.Height > 0)
                    {
                        parsed.FillType = SKPathFillType.Winding;
                        return parsed;
                    }
                    parsed?.Dispose();
                }
                catch
                {
                    // Fall through to an empty path.
                    System.Diagnostics.Debug.WriteLine("Failed to parse SVG path data; returning empty path");
                }
                return new SKPath();
            });
        }
    }
}
