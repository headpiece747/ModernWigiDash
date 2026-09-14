namespace ModernWigiDash.Widgets;

/// <summary>
/// The shared one-reading display renderer for the telemetry widgets (the
/// Hardware Monitor and HWiNFO Sensor widgets): the gauge, bar, value, and
/// sparkline-graph layouts over a <see cref="SystemTelemetryDisplay"/>, with the
/// hoisted per-frame paints, the bounded sample history, and the caller-owned
/// sparkline paths. Owns exactly the pixel behavior the two widgets share; each
/// widget keeps its own data source, gates, and property surface.
/// </summary>
internal sealed class TelemetryReadingRenderer : IDisposable
{
    /// <summary>The shared sparkline/history bound.</summary>
    internal const int HistoryCapacity = 96;

    private readonly Queue<float> _history = new();

    // Hoisted paints: the colors mutate per render (property-driven), so the
    // 30 FPS render allocates no SKPaint — the gauge/bar strokes and the
    // sparkline line/fill included.
    private readonly SKPaint _headerPaint = new() { IsAntialias = true };
    private readonly SKPaint _valuePaint = new() { IsAntialias = true };
    private readonly SKPaint _unitPaint = new() { IsAntialias = true };
    private readonly SKPaint _gaugeTrackPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    private readonly SKPaint _gaugeProgressPaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    private readonly SKPaint _barTrackPaint = new() { IsAntialias = true };
    private readonly SKPaint _barProgressPaint = new() { IsAntialias = true };
    private readonly SKPaint _sparkFillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _sparkLinePaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
    private readonly SKPaint _placeholderTitlePaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderSubPaint = new() { IsAntialias = true };

    // The graph mode's sparkline paths are caller-owned and rewound per frame
    // (the history appends a sample every render, so the geometry is never
    // stable — the paths themselves must not be reallocated either).
    private SKPath? _sparkLinePath;
    private SKPath? _sparkFillPath;
    private bool _disposed;

    /// <summary>Internal test accessor: how many history samples are buffered.</summary>
    internal int HistoryCountForTest => _history.Count;

    /// <summary>
    /// The highest buffered sample (0 when empty). The HWiNFO widget has no
    /// vendor-reported maximum, so it feeds this to the presentation's
    /// auto-scale reference instead.
    /// </summary>
    internal float HistoryMax
    {
        get
        {
            float max = 0f;
            foreach (float sample in _history)
            {
                // Ternary rather than Math.Max: a NaN sample (a not-ready vendor
                // reading) must be ignored, and Math.Max(0, NaN) is NaN.
                max = sample > max ? sample : max;
            }

            return max;
        }
    }

    /// <summary>
    /// Appends the value to the bounded sample history, then draws the display
    /// in its mode. The history feeds both the sparkline and the auto-scale
    /// reference, so it is appended in every mode.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="bounds">The widget's bounds in canvas coordinates.</param>
    /// <param name="display">The reading display state (mode, strings, progress).</param>
    /// <param name="value">The current reading.</param>
    /// <param name="readingMin">The source's recorded minimum (0 when it has none).</param>
    /// <param name="readingMax">The source's recorded maximum (0 when it has none).</param>
    /// <param name="accent">The accent color.</param>
    /// <param name="text">The text color.</param>
    internal void Render(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, float value, double readingMin, double readingMax, SKColor accent, SKColor text)
    {
        _history.Enqueue(value);
        while (_history.Count > HistoryCapacity)
        {
            _history.Dequeue();
        }

        switch (display.Mode)
        {
            case SystemTelemetryDisplayMode.Bar:
                RenderBar(canvas, bounds, display, accent, text);
                break;
            case SystemTelemetryDisplayMode.Value:
                RenderValue(canvas, bounds, display, text);
                break;
            case SystemTelemetryDisplayMode.Graph:
                RenderGraph(canvas, bounds, display, value, readingMin, readingMax, accent, text);
                break;
            default:
                RenderGauge(canvas, bounds, display, accent, text);
                break;
        }
    }

    /// <summary>Draws the shared title/subtitle unavailable placeholder.</summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="bounds">The widget's bounds in canvas coordinates.</param>
    /// <param name="title">The placeholder title.</param>
    /// <param name="subtitle">The placeholder subtitle.</param>
    /// <param name="text">The text color.</param>
    internal void DrawPlaceholder(SKCanvas canvas, SKRect bounds, string title, string subtitle, SKColor text)
        => TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, title, subtitle, text, _placeholderTitlePaint, _placeholderSubPaint);

    /// <summary>
    /// Draws the header label (scaled down on short widgets, truncated to the
    /// widget width) and returns the font size used, so the caller can reserve
    /// the content region below it.
    /// </summary>
    private float DrawHeader(SKCanvas canvas, SKRect bounds, string label, float pad, SKColor text)
    {
        float headerSize = Math.Clamp(bounds.Height * 0.15f, 11f, 22f);
        var headerFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, headerSize);
        _headerPaint.Color = text;
        canvas.DrawTextWithFallback(
            TextRenderHelper.TruncateText(label, headerFont, bounds.Width - pad * 2f),
            bounds.Left + pad,
            bounds.Top + pad + headerSize,
            headerFont,
            _headerPaint);
        return headerSize;
    }

    /// <summary>
    /// The largest cached Geist-bold size at or below <paramref name="maxSize"/>
    /// (and at or above <paramref name="minSize"/>) at which the text fits
    /// <paramref name="maxWidth"/>. Stepped by 10% to bound the font cache.
    /// </summary>
    internal static SKFont FitFont(SKTypeface typeface, string text, float maxWidth, float maxSize, float minSize)
    {
        float floor = Math.Max(1f, minSize);
        float size = Math.Max(floor, maxSize);
        while (size > floor)
        {
            var font = FontHelper.GetCachedFont(typeface, size);
            if (FontHelper.MeasureTextWithFallback(text, font) <= maxWidth)
            {
                return font;
            }

            size = Math.Max(floor, size * 0.9f);
        }

        return FontHelper.GetCachedFont(typeface, floor);
    }

    /// <summary>
    /// Draws the big hero value with its trailing unit — the "value + unit"
    /// block shared by the four display modes. Per-mode spacing stays at the
    /// call sites: value font size, baseline anchor, unit font size, and the
    /// unit's pixel offset from the value (the +4/+5/+6 deltas are pixel
    /// behavior, not duplication).
    /// </summary>
    private void DrawHeroValue(
        SKCanvas canvas,
        SystemTelemetryDisplay display,
        float anchorX,
        float baselineAnchor,
        float baselineFromValue,
        float valFontSize,
        SKColor valueColor,
        SKColor unitColor,
        float unitFontSize,
        float unitOffset,
        float maxWidth,
        float minFontSize,
        bool rightAligned = false)
    {
        string valStr = display.ValueText;
        bool hasUnit = !string.IsNullOrWhiteSpace(display.Unit);

        var requestedUnitFont = hasUnit ? FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, unitFontSize) : null;
        float reserved = hasUnit
            ? FontHelper.MeasureTextWithFallback(display.Unit, requestedUnitFont!) + unitOffset
            : 0f;
        float valueBudget = Math.Max(1f, maxWidth - reserved);

        // Shrink the hero value (plus its reserved unit) until it fits the
        // widget, then ellipsize if even the floor overflows. Without this the
        // value ran past the edge at small sizes and for long readings.
        var valFont = FitFont(FontHelper.GeistTypeface, valStr, valueBudget, valFontSize, minFontSize);
        valStr = TextRenderHelper.TruncateText(valStr, valFont, valueBudget);

        _valuePaint.Color = valueColor;
        valFont.MeasureText(valStr, out var valBounds, _valuePaint);
        float valWidth = FontHelper.MeasureTextWithFallback(valStr, valFont);

        // The unit scales down with the value so the pair stays in proportion;
        // the pair is then centered/right-aligned as ONE block, so the trailing
        // unit can never push past the widget edge on its own.
        SKFont? unitFont = null;
        float unitWidth = 0f;
        float drawnUnitOffset = 0f;
        if (hasUnit)
        {
            float ratio = valFontSize > 0f ? valFont.Size / valFontSize : 1f;
            unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(1f, unitFontSize * ratio));
            unitWidth = FontHelper.MeasureTextWithFallback(display.Unit, unitFont);
            drawnUnitOffset = unitOffset * ratio;
        }

        float totalWidth = valWidth + (hasUnit ? drawnUnitOffset + unitWidth : 0f);
        float valueX = rightAligned ? anchorX - totalWidth : anchorX - totalWidth / 2f;
        float baselineY = baselineAnchor + valBounds.Height * baselineFromValue;
        canvas.DrawTextWithFallback(valStr, valueX, baselineY, valFont, _valuePaint);

        if (hasUnit)
        {
            _unitPaint.Color = unitColor;
            canvas.DrawTextWithFallback(display.Unit, valueX + valWidth + drawnUnitOffset, baselineY, unitFont!, _unitPaint);
        }
    }

    private void RenderGauge(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, SKColor accent, SKColor text)
    {
        float pad = 16f;
        float headerSize = DrawHeader(canvas, bounds, display.Label, pad, text);
        float contentTop = bounds.Top + pad + headerSize + 4f;
        float contentBottom = bounds.Bottom - pad;
        float contentHeight = Math.Max(1f, contentBottom - contentTop);
        float contentMidY = (contentTop + contentBottom) / 2f;
        float contentWidth = bounds.Width - pad * 2f;

        float gaugeSize = Math.Max(24f, Math.Min(contentWidth * 0.42f, contentHeight));
        float cx = bounds.MidX;
        float cy = contentMidY;
        float radius = Math.Max(1f, (gaugeSize / 2f) - 10f);
        var arcBounds = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);

        _gaugeTrackPaint.Color = text.WithAlpha(20);
        canvas.DrawArc(arcBounds, 135f, 270f, false, _gaugeTrackPaint);

        float progress = display.Progress;
        _gaugeProgressPaint.Color = accent;
        canvas.DrawArc(arcBounds, 135f, 270f * progress, false, _gaugeProgressPaint);

        DrawHeroValue(canvas, display, cx, cy, 1f / 3f, gaugeSize * 0.2f, text, text.WithAlpha(180), 11f, 4f,
            GaugeValueMaxWidth(gaugeSize), Math.Max(8f, gaugeSize * 0.09f));
    }

    /// <summary>
    /// The gauge hero value's maximum width: the arc's inner diameter (the
    /// diameter minus the 12px stroke) minus a clearance, so the value always
    /// sits INSIDE the arc and never touches it.
    /// </summary>
    internal static float GaugeValueMaxWidth(float gaugeSize) => Math.Max(8f, gaugeSize - 40f);

    private void RenderBar(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, SKColor accent, SKColor text)
    {
        float pad = 16f;
        float headerSize = DrawHeader(canvas, bounds, display.Label, pad, text);
        float contentTop = bounds.Top + pad + headerSize + 4f;
        float contentBottom = bounds.Bottom - pad;
        float contentHeight = Math.Max(1f, contentBottom - contentTop);
        float contentMidY = (contentTop + contentBottom) / 2f;
        float contentWidth = bounds.Width - pad * 2f;

        float barHeight = Math.Clamp(contentHeight * 0.08f, 8f, 12f);
        var barRect = new SKRect(bounds.Left + pad, contentBottom - barHeight, bounds.Right - pad, contentBottom);
        float trackRadius = barRect.Height / 2f;

        DrawHeroValue(canvas, display, bounds.MidX, contentMidY - barHeight, 0f,
            Math.Clamp(contentHeight * 0.24f, 18f, 48f), text, text.WithAlpha(180), 12f, 5f,
            contentWidth, 9f);

        _barTrackPaint.Color = text.WithAlpha(20);
        canvas.DrawRoundRect(barRect, trackRadius, trackRadius, _barTrackPaint);

        float progress = display.Progress;
        float progressWidth = Math.Max(barRect.Height, barRect.Width * progress);
        var progressRect = new SKRect(barRect.Left, barRect.Top, barRect.Left + progressWidth, barRect.Bottom);
        float progressRadius = Math.Min(trackRadius, progressWidth / 2f);
        _barProgressPaint.Color = accent;
        canvas.DrawRoundRect(progressRect, progressRadius, progressRadius, _barProgressPaint);
    }

    private void RenderValue(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, SKColor text)
    {
        float pad = 16f;
        float headerSize = DrawHeader(canvas, bounds, display.Label, pad, text);
        float contentTop = bounds.Top + pad + headerSize + 4f;
        float contentBottom = bounds.Bottom - pad;
        float contentHeight = Math.Max(1f, contentBottom - contentTop);
        float contentMidY = (contentTop + contentBottom) / 2f;
        float contentWidth = bounds.Width - pad * 2f;

        DrawHeroValue(canvas, display, bounds.MidX, contentMidY + 4f, 0f,
            Math.Min(contentWidth * 0.22f, contentHeight * 0.42f), text, text.WithAlpha(180), 14f, 6f,
            contentWidth, 9f);
    }

    private void RenderGraph(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, float value, double readingMin, double readingMax, SKColor accent, SKColor text)
    {
        float pad = 16f;
        float headerSize = DrawHeader(canvas, bounds, display.Label, pad, text);
        float contentTop = bounds.Top + pad + headerSize + 4f;
        float contentBottom = bounds.Bottom - pad;

        // The value readout owns a band directly under the header; the
        // sparkline starts below the band, so the graph line never runs behind
        // the text.
        float valFontSize = Math.Clamp(Math.Max(1f, contentBottom - contentTop) * 0.14f, 12f, 22f);
        float valueBandHeight = valFontSize * 1.3f;
        var plot = new SKRect(bounds.Left + pad, contentTop + valueBandHeight + 2f, bounds.Right - pad, contentBottom);

        int count = _history.Count;
        if (count >= 2)
        {
            // Zero-alloc: copy the float history onto the stack for the
            // sparkline, computing min/max in the same single pass (replaces
            // Cast<double>().ToList() + Min() + Max() per frame).
            Span<float> samples = count <= HistoryCapacity
                ? stackalloc float[count]
                : new float[count];
            float min = float.MaxValue;
            float max = float.MinValue;
            int i = 0;
            foreach (float sample in _history)
            {
                samples[i++] = sample;
                if (sample < min) min = sample;
                if (sample > max) max = sample;
            }

            float lo = Math.Min(min, (float)readingMin);
            float hi = Math.Max(max, (float)readingMax);
            if (hi - lo < 1e-6f)
            {
                lo = value - 1f;
                hi = value + 1f;
            }

            // The caller-owned paths are rewound per frame (the sample history
            // changes every frame, so the geometry is never stable — the paths
            // themselves are not reallocated either), and the hoisted paints
            // are re-colored per frame.
            _sparkFillPath ??= new SKPath();
            _sparkLinePath ??= new SKPath();
            SparklineRenderer.RebuildSparklinePaths(plot, samples, lo, hi, _sparkLinePath, _sparkFillPath);
            _sparkFillPaint.Color = accent.WithAlpha(40);
            canvas.DrawPath(_sparkFillPath, _sparkFillPaint);
            _sparkLinePaint.Color = accent;
            canvas.DrawPath(_sparkLinePath, _sparkLinePaint);
        }

        DrawHeroValue(canvas, display, bounds.Right - pad, contentTop + valFontSize, 0f,
            valFontSize, accent, text.WithAlpha(180), 11f, 4f, bounds.Width - pad * 2f, 9f, rightAligned: true);
    }

    /// <summary>Disposes the hoisted paints and the caller-owned sparkline paths.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _headerPaint.Dispose();
        _valuePaint.Dispose();
        _unitPaint.Dispose();
        _gaugeTrackPaint.Dispose();
        _gaugeProgressPaint.Dispose();
        _barTrackPaint.Dispose();
        _barProgressPaint.Dispose();
        _sparkFillPaint.Dispose();
        _sparkLinePaint.Dispose();
        _placeholderTitlePaint.Dispose();
        _placeholderSubPaint.Dispose();
        _sparkLinePath?.Dispose();
        _sparkFillPath?.Dispose();
    }
}
