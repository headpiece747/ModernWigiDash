namespace ModernWigiDash.Widgets;

/// <summary>
/// Live FPS / frame-time dashboard: current FPS, frame time, 1% low, 0.1% low,
/// GPU busy %, and CPU frame time for the focused game/app, plus a rolling
/// frame-time sparkline. Frame times come from the PresentMon Service
/// (ADR-0003): the app opens a non-elevated session, starts tracking the
/// focused process, and polls the counters on the 1s poll loop. When the
/// service is absent, the widget renders the graceful unavailable state; with
/// no tracked process, the dashboard renders zero values — PresentMon's own
/// value for no presents (0 presents/sec).
/// </summary>
[WidgetMetadata("frame_time", "FPS / Frame Time", Category = "System Monitoring")]
public class FrameTimeWidget : ModernWidgetBase
{
    /// <summary>The "Accent Color" property: primary accent color.</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Text Color" property: header, label, and value color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Header, label, and value color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    /// <summary>The "Show Process" property: show the tracked game/process name.</summary>
    [WidgetProperty("Show Process", WidgetPropertyType.Boolean, "Show the tracked game/process name", true)]
    public bool ShowProcess { get; set; } = true;

    /// <summary>Test seam: current view (false = dashboard, true = overlay readout).</summary>
    internal bool IsOverlayView { get; set; }

    // Hoisted paints: fixed-color paints stay constant; the accent/text-driven
    // ones mutate Color per render, so the 30 FPS render allocates no SKPaint.
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
    private bool _disposed;

    // The presentation model is memoized on (snapshot reference, placement
    // size): the store hands the same snapshot back for ~30 frames (the
    // producer polls at 1/s), so the ~40-object rebuild runs once per second
    // instead of once per frame.
    private FrameTimeSnapshotDto? _memoSnapshot;
    private SKSize _memoSize;
    private FrameTimeDisplay? _memoDisplay;

    // The truncated process name rides its own copy of the same key
    // (snapshot reference, placement size): TruncateText allocates a fresh
    // string per frame while the tracked view is shown, but the name and the
    // width are stable between snapshots.
    private FrameTimeSnapshotDto? _processTextSnapshot;
    private SKSize _processTextSize;
    private string? _processText;

    private FrameTimeDisplay BuildDisplay(FrameTimeSnapshotDto snapshot, SKSize size)
    {
        if (_memoDisplay is not null && ReferenceEquals(snapshot, _memoSnapshot) && _memoSize == size)
        {
            return _memoDisplay;
        }

        _memoSnapshot = snapshot;
        _memoSize = size;
        _memoDisplay = FrameTimePresentation.Build(snapshot, size);
        return _memoDisplay;
    }

    /// <summary>
    /// Draws the frame-time view (dashboard or overlay) from the store's
    /// fresh snapshot, or the graceful unavailable placeholder when the
    /// PresentMon capture is absent.
    /// </summary>
    /// <param name="canvas">The frame canvas.</param>
    /// <param name="bounds">The widget's placement bounds.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);
        SKColor text = ColorOf(TextColorHex, SKColors.White);

        // The store owns the staleness decision; a stale snapshot renders the
        // unavailable state instead of frozen data.
        FrameTimeSnapshotDto? snapshot = FrameTimeStore.TryReadFresh();
        if (snapshot is null || !snapshot.IsAvailable)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "Frame capture unavailable", "Install and run the PresentMon Service", text, _placeholderTitlePaint, _placeholderSubPaint);
            return;
        }

        if (!snapshot.CaptureHealthy)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "PresentMon capture inactive", "The service is not producing present data", text, _placeholderTitlePaint, _placeholderSubPaint);
            return;
        }

        var display = BuildDisplay(snapshot, bounds.Size);
        if (IsOverlayView)
        {
            RenderOverlayView(canvas, bounds, text, display);
            return;
        }

        RenderTrackedView(canvas, bounds, accent, text, snapshot, display);
    }

    /// <summary>
    /// The dashboard view: hero FPS + frame time, process name, up to eight
    /// metric cards, and the frame-time sparkline. The no-process state
    /// renders the same layout with zero values — the presentation model
    /// decides what every string reads and which rows the placement size
    /// keeps.
    /// </summary>
    private void RenderTrackedView(SKCanvas canvas, SKRect bounds, SKColor accent, SKColor text, FrameTimeSnapshotDto snapshot, FrameTimeDisplay display)
    {
        float pad = Math.Clamp(bounds.Height * 0.05f, 10f, 22f);

        bool tiny = display.IsCompact; // the model owns the 150px breakpoint
        float graphHeight = display.ShowGraph ? bounds.Height * 0.12f : 0f;

        float contentTop = bounds.Top + pad;
        float contentBottom = bounds.Bottom - pad - (display.ShowGraph ? graphHeight + 6f : 0f);

        float heroTop = contentTop;
        if (!tiny && ShowProcess && display.ShowProcessName)
        {
            float procSize = Math.Clamp((contentBottom - contentTop) * 0.08f, 10f, 15f);
            var processFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, procSize);
            _processPaint.Color = text.WithAlpha(180);
            if (_processText is null
                || !ReferenceEquals(snapshot, _processTextSnapshot)
                || _processTextSize != bounds.Size)
            {
                _processText = TextRenderHelper.TruncateText(display.ProcessName, processFont, bounds.Width - pad * 2f);
                _processTextSnapshot = snapshot;
                _processTextSize = bounds.Size;
            }
            string process = _processText;
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

                // One paint pair per frame for all metric cards (4-8 draws).
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
            SKRect graphArea = new SKRect(bounds.Left + pad, bounds.Bottom - pad - graphHeight, bounds.Right - pad, bounds.Bottom - pad);
            DrawCachedSparkline(canvas, graphArea, snapshot.RecentFrameTimesMs, accent);
        }
    }

    /// <summary>A release toggles the dashboard/overlay readout view and requests a render.</summary>
    /// <param name="localPoint">The touch point in the widget's rotated-local space.</param>
    /// <param name="eventType">The touch event type.</param>
    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchUp)
        {
            IsOverlayView = !IsOverlayView;
            Context?.RequestRender();
        }
    }

    /// <summary>Disposes the widget's hoisted Skia paints.</summary>
    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
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
        return base.DisposeAsync();
    }

    /// <summary>
    /// PresentMon-overlay-style readout (view C): the metric lines PresentMon's
    /// own overlay lists, in the project font and the widget's colors. The
    /// lines and their shrink clip come from the presentation model; this
    /// method only lays them out.
    /// </summary>
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

    private IReadOnlyList<double>? _lastSparkSamples;
    private SKPath? _sparkFill;
    private SKPath? _sparkLine;

    /// <summary>
    /// The sparkline path is rebuilt only when the samples change (~1/s); the
    /// render tick just draws the cached path.
    /// </summary>
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
}

