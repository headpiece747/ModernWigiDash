using System.Buffers;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The sparkline module: builds the line + fill paths for telemetry samples
/// and draws them. Owned by the two telemetry widgets — the hardware
/// monitor's graph mode rebuilds its caller-owned paths in place every
/// frame (RebuildSparklinePaths), the frame-time widget caches the paths it
/// builds (BuildSparklinePaths) — the geometry rule is implemented once, in
/// the span builder; the list entry points adapt to it.
/// </summary>
internal static class SparklineRenderer
{
    /// <summary>
    /// Draws a line-plus-fill sparkline for <paramref name="samples"/>
    /// normalized into the <paramref name="lo"/>..<paramref name="hi"/> range
    /// inside <paramref name="area"/>.
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

    /// <summary>
    /// Rebuilds the line + fill paths IN PLACE (rewinding the caller-owned
    /// paths): the 30 FPS graph mode where the samples change every frame, so
    /// the render tick allocates no SKPath. The out-variants keep building
    /// fresh paths for the renderer that caches them (the frame-time widget
    /// rebuilds only when the samples identity changes, ~1/s).
    /// </summary>
    internal static void RebuildSparklinePaths(SKRect area, ReadOnlySpan<float> samples, float lo, float hi, SKPath line, SKPath fill)
    {
        if (samples.Length < 2) return;

        if (hi - lo == 0f)
        {
            lo -= 1f;
            hi += 1f;
        }

        float span = area.Width / Math.Max(1, samples.Length - 1);
#pragma warning disable CS0618 // SKPath.Rewind/MoveTo/LineTo/Close are obsolete in favor of SKPathBuilder, whose Snapshot()/Detach() allocate a new SKPath per call — the caller-owned paths are rewound and re-lined instead (zero-alloc hot path).
        fill.Rewind();
        line.Rewind();
        for (int i = 0; i < samples.Length; i++)
        {
            float x = area.Left + i * span;
            float y = area.Bottom - (samples[i] - lo) / (hi - lo) * area.Height;
            if (i == 0)
            {
                line.MoveTo(x, y);
                fill.MoveTo(x, y);
            }
            else
            {
                line.LineTo(x, y);
                fill.LineTo(x, y);
            }
        }

        fill.LineTo(area.Right, area.Bottom);
        fill.LineTo(area.Left, area.Bottom);
        fill.Close();
#pragma warning restore CS0618
    }
}
