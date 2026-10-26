namespace ModernWigiDash.Widgets;

/// <summary>
/// Encapsulates zero-allocation Skia rendering for the FPS and frame-time widget.
/// Hoists all 12 <see cref="SKPaint"/> instances, caches presentation models and sparkline
/// paths, and handles hero FPS typography, metric cards, sparklines, and overlay mode.
/// </summary>
internal sealed class FrameTimeCanvas : IDisposable
{
    private readonly SKPaint _processPaint = new() { IsAntialias = true };
    private readonly SKPaint _fpsPaint = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint _unitPaint = new() { IsAntialias = true };
    private readonly SKPaint _msPaint = new() { IsAntialias = true };
    private readonly SKPaint _valPaint = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint _lblPaint = new() { IsAntialias = true };
    private readonly SKPaint _labelPaint = new() { IsAntialias = true };
    private readonly SKPaint _valuePaint = new() { Color = SKColors.White, IsAntialias = true };
    private readonly SKPaint _fillPaint = new() { Style = SKPaintStyle.Fill, IsAntialias = true };
    private readonly SKPaint _linePaint = new() { Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
    private readonly SKPaint _placeholderTitlePaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderSubPaint = new() { IsAntialias = true };

    // The presentation memos: keyed by the published snapshot's REFERENCE
    // and the display size (the producer publishes a fresh record each tick,
    // so a reference change is a content change). The shared memo slot owns
    // the key-compare; the key comparer carries the reference-identity rule.
    private readonly MemoSlot<FrameTimeDisplayKey, FrameTimeDisplay> _displayMemo = new(FrameTimeKeyComparer.Instance);
    private readonly MemoSlot<FrameTimeDisplayKey, string> _processTextMemo = new(FrameTimeKeyComparer.Instance);

    /// <summary>The frame-time memo key: the snapshot reference + the display size.</summary>
    private readonly record struct FrameTimeDisplayKey(FrameTimeSnapshotDto Snapshot, SKSize Size);

    /// <summary>The key's equality: the snapshot compares by REFERENCE (a fresh
    /// record is a content change), the size by value.</summary>
    private sealed class FrameTimeKeyComparer : IEqualityComparer<FrameTimeDisplayKey>
    {
        public static readonly FrameTimeKeyComparer Instance = new();

        public bool Equals(FrameTimeDisplayKey x, FrameTimeDisplayKey y)
            => x.Size == y.Size && ReferenceEquals(x.Snapshot, y.Snapshot);

        public int GetHashCode(FrameTimeDisplayKey key)
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Snapshot), key.Size);
    }

    private IReadOnlyList<double>? _lastSparkSamples;
    private SKPath? _sparkFill;
    private SKPath? _sparkLine;

    private FrameTimeDisplay BuildDisplay(FrameTimeSnapshotDto snapshot, SKSize size)
    {
        var key = new FrameTimeDisplayKey(snapshot, size);
        if (_displayMemo.TryGet(key, out var value))
        {
            return value;
        }

        value = FrameTimePresentation.Build(snapshot, size);
        _displayMemo.Set(key, value);
        return value;
    }

    /// <summary>
    /// Renders the unavailable / inactive placeholder when PresentMon capture is down.
    /// </summary>
    public void RenderPlaceholder(SKCanvas canvas, SKRect bounds, string title, string subtitle, SKColor text)
    {
        TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, title, subtitle, text, _placeholderTitlePaint, _placeholderSubPaint);
    }

    /// <summary>
    /// Renders the frame-time view (either dashboard or overlay readout mode).
    /// </summary>
    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        FrameTimeSnapshotDto snapshot,
        bool isOverlayView,
        bool showProcess,
        SKColor accent,
        SKColor text)
    {
        var display = BuildDisplay(snapshot, bounds.Size);
        if (isOverlayView)
        {
            RenderOverlayView(canvas, bounds, text, display);
            return;
        }

        RenderTrackedView(canvas, bounds, accent, text, snapshot, display, showProcess);
    }

    private void RenderTrackedView(
        SKCanvas canvas,
        SKRect bounds,
        SKColor accent,
        SKColor text,
        FrameTimeSnapshotDto snapshot,
        FrameTimeDisplay display,
        bool showProcess)
    {
        float pad = Math.Clamp(bounds.Height * 0.05f, 10f, 22f);

        bool tiny = display.IsCompact;
        float graphHeight = display.ShowGraph ? bounds.Height * 0.12f : 0f;

        float contentTop = bounds.Top + pad;
        float contentBottom = bounds.Bottom - pad - (display.ShowGraph ? graphHeight + 6f : 0f);

        float heroTop = contentTop;
        if (!tiny && showProcess && display.ShowProcessName)
        {
            float procSize = Math.Clamp((contentBottom - contentTop) * 0.08f, 10f, 15f);
            var processFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, procSize);
            _processPaint.Color = text.WithAlpha(180);
            var processKey = new FrameTimeDisplayKey(snapshot, bounds.Size);
            string process;
            if (!_processTextMemo.TryGet(processKey, out process))
            {
                process = TextRenderHelper.TruncateText(display.ProcessName, processFont, bounds.Width - pad * 2f);
                _processTextMemo.Set(processKey, process);
            }
            canvas.DrawTextWithFallback(process, bounds.Right - pad - FontHelper.MeasureTextWithFallback(process, processFont), contentTop + procSize, processFont, _processPaint);
            heroTop = contentTop + procSize + 6f;
        }

        float heroBottom = display.ShowMetricCards ? contentTop + (contentBottom - contentTop) * 0.45f : contentBottom;
        float heroH = Math.Max(8f, heroBottom - heroTop);

        float fpsFontSize = Math.Clamp(heroH * 0.85f, 24f, 120f);
        var fpsFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize);

        string fpsText = display.HeroFps;
        fpsFont.MeasureText(fpsText, out var fpsBounds, _fpsPaint);
        float fpsX = bounds.Left + pad;
        float fpsBaseline = heroTop + fpsFontSize * 0.82f;
        canvas.DrawTextWithFallback(fpsText, fpsX, fpsBaseline, fpsFont, _fpsPaint);

        float unitX = fpsX + fpsBounds.Width + 10f;
        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.32f);
        _unitPaint.Color = accent;
        canvas.DrawTextWithFallback("FPS", unitX, heroTop + fpsFontSize * 0.38f, unitFont, _unitPaint);

        var msFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.36f);
        _msPaint.Color = text.WithAlpha(220);
        canvas.DrawTextWithFallback(display.HeroFrameTimeMs, unitX, fpsBaseline, msFont, _msPaint);

        if (display.ShowMetricCards)
        {
            float gridTop = heroBottom + 4f;
            float gridH = contentBottom - gridTop;
            if (gridH >= 24f)
            {
                float colWidth = (bounds.Width - pad * 2f) / 4f;
                float metricValSize = Math.Clamp(gridH * 0.40f, 12f, 32f);
                float metricLblSize = Math.Clamp(gridH * 0.25f, 9f, 18f);
                float row1Top = gridTop;
                float row2Top = gridTop + gridH * 0.52f;

                _valPaint.Color = SKColors.White;
                _lblPaint.Color = accent;

                for (int i = 0; i < 4; i++)
                {
                    DrawMetricCard(canvas, bounds.Left + pad + colWidth * (i + 0.5f), row1Top,
                        display.Dashboard[i].Label, display.Dashboard[i].Value, metricValSize, metricLblSize, _valPaint, _lblPaint);
                }

                if (display.ShowSecondRow)
                {
                    for (int i = 4; i < 8; i++)
                    {
                        DrawMetricCard(canvas, bounds.Left + pad + colWidth * (i - 3.5f), row2Top,
                            display.Dashboard[i].Label, display.Dashboard[i].Value, metricValSize, metricLblSize, _valPaint, _lblPaint);
                    }
                }
            }
        }

        if (display.ShowGraph)
        {
            SKRect graphArea = new(bounds.Left + pad, bounds.Bottom - pad - graphHeight, bounds.Right - pad, bounds.Bottom - pad);
            DrawCachedSparkline(canvas, graphArea, snapshot.RecentFrameTimesMs, accent);
        }
    }

    private void RenderOverlayView(SKCanvas canvas, SKRect bounds, SKColor text, FrameTimeDisplay display)
    {
        float pad = Math.Clamp(bounds.Height * 0.06f, 8f, 20f);
        float fontSize = Math.Clamp(bounds.Height * 0.052f, 10f, 24f);

        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontSize);
        _labelPaint.Color = text.WithAlpha(180);
        float lineHeight = fontSize * 1.45f;

        float x = bounds.Left + pad;
        for (int i = 0; i < display.OverlayLineCount; i++)
        {
            float y = bounds.Top + pad + (i + 1) * lineHeight;
            canvas.DrawTextWithFallback(display.Overlay[i].Label, x, y, font, _labelPaint, SKTextAlign.Left);
            canvas.DrawTextWithFallback(display.Overlay[i].Value, bounds.Right - pad, y, font, _valuePaint, SKTextAlign.Right);
        }
    }

    private void DrawCachedSparkline(SKCanvas canvas, SKRect area, IReadOnlyList<double> samples, SKColor accent)
    {
        if (!ReferenceEquals(samples, _lastSparkSamples))
        {
            _sparkFill?.Dispose();
            _sparkLine?.Dispose();
            _sparkFill = null;
            _sparkLine = null;
            _lastSparkSamples = samples;
            if (samples.Count >= 2)
            {
                double lo = samples.Min();
                double hi = samples.Max();
                if (hi - lo < 0.001)
                {
                    lo -= 1;
                    hi += 1;
                }
                SparklineRenderer.BuildSparklinePaths(area, samples, lo, hi, out _sparkLine, out _sparkFill);
            }
        }

        if (_sparkLine is null || _sparkFill is null) return;

        _fillPaint.Color = accent.WithAlpha(40);
        canvas.DrawPath(_sparkFill, _fillPaint);
        _linePaint.Color = accent;
        canvas.DrawPath(_sparkLine, _linePaint);
    }

    private static void DrawMetricCard(SKCanvas canvas, float cx, float topY, string label, string value, float valSize, float lblSize, SKPaint valPaint, SKPaint lblPaint)
    {
        var valFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, valSize);
        valFont.MeasureText(value, out var valBounds, valPaint);
        float valY = topY + valSize * 0.85f;
        canvas.DrawTextWithFallback(value, cx - valBounds.Width / 2f, valY, valFont, valPaint);

        var lblFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, lblSize);
        lblFont.MeasureText(label, out var lblBounds, lblPaint);
        float lblY = valY + lblSize + 4f;
        canvas.DrawTextWithFallback(label, cx - lblBounds.Width / 2f, lblY, lblFont, lblPaint);
    }

    /// <summary>Disposes all hoisted Skia resources.</summary>
    public void Dispose()
    {
        _sparkFill?.Dispose();
        _sparkLine?.Dispose();
        _processPaint.Dispose();
        _fpsPaint.Dispose();
        _unitPaint.Dispose();
        _msPaint.Dispose();
        _valPaint.Dispose();
        _lblPaint.Dispose();
        _labelPaint.Dispose();
        _valuePaint.Dispose();
        _fillPaint.Dispose();
        _linePaint.Dispose();
        _placeholderTitlePaint.Dispose();
        _placeholderSubPaint.Dispose();
    }
}
