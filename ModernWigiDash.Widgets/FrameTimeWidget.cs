using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Sdk;
using SkiaSharp;

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
    public override SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Header, label, and value color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Show Process", WidgetPropertyType.Boolean, "Show the tracked game/process name", true)]
    public bool ShowProcess { get; set; } = true;

    /// <summary>Test seam: current view (false = dashboard, true = overlay readout).</summary>
    internal bool IsOverlayView { get; set; }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor accent = ColorOf(AccentColorHex, new SKColor(255, 205, 133));
        SKColor text = ColorOf(TextColorHex, SKColors.White);

        // The store owns the staleness decision; a stale snapshot renders the
        // unavailable state instead of frozen data.
        FrameTimeSnapshotDto? snapshot = FrameTimeStore.TryReadFresh();
        if (snapshot is null || !snapshot.IsAvailable)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "Frame capture unavailable", "Install and run the PresentMon Service", text);
            return;
        }

        if (!snapshot.CaptureHealthy)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "PresentMon capture inactive", "The service is not producing present data", text);
            return;
        }

        var display = FrameTimePresentation.Build(snapshot, bounds.Size);
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
            using var processPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            string process = TextRenderHelper.TruncateText(display.ProcessName, processFont, bounds.Width - pad * 2f);
            canvas.DrawTextWithFallback(process, bounds.Right - pad - FontHelper.MeasureTextWithFallback(process, processFont), contentTop + procSize, processFont, processPaint);
            heroTop = contentTop + procSize + 6f;
        }

        float heroBottom = display.ShowMetricCards ? contentTop + (contentBottom - contentTop) * 0.45f : contentBottom;
        float heroH = Math.Max(8f, heroBottom - heroTop);

        float fpsFontSize = Math.Clamp(heroH * 0.85f, 24f, 120f);
        var fpsFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize);
        using var fpsPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        string fpsText = display.HeroFps;
        fpsFont.MeasureText(fpsText, out var fpsBounds, fpsPaint);
        float fpsX = bounds.Left + pad;
        float fpsBaseline = heroTop + fpsFontSize * 0.82f;
        canvas.DrawTextWithFallback(fpsText, fpsX, fpsBaseline, fpsFont, fpsPaint);

        float unitX = fpsX + fpsBounds.Width + 10f;
        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.32f);
        using var unitPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawTextWithFallback("FPS", unitX, heroTop + fpsFontSize * 0.38f, unitFont, unitPaint);

        var msFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.36f);
        using var msPaint = new SKPaint { Color = text.WithAlpha(220), IsAntialias = true };
        canvas.DrawTextWithFallback(display.HeroFrameTimeMs, unitX, fpsBaseline, msFont, msPaint);

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

                for (int i = 0; i < 4; i++)
                {
                    DrawMetricCard(canvas, bounds.Left + pad + colWidth * (i + 0.5f), row1Top,
                        display.Dashboard[i].Label, display.Dashboard[i].Value, metricValSize, metricLblSize, accent);
                }

                if (display.ShowSecondRow)
                {
                    for (int i = 4; i < 8; i++)
                    {
                        DrawMetricCard(canvas, bounds.Left + pad + colWidth * (i - 3.5f), row2Top,
                            display.Dashboard[i].Label, display.Dashboard[i].Value, metricValSize, metricLblSize, accent);
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

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchUp)
        {
            IsOverlayView = !IsOverlayView;
            Context?.RequestRender();
        }
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
        using var labelPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
        using var valuePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float lineHeight = fontSize * 1.45f;

        float x = bounds.Left + pad;
        for (int i = 0; i < display.OverlayLineCount; i++)
        {
            float y = bounds.Top + pad + (i + 1) * lineHeight;
            canvas.DrawTextWithFallback(display.Overlay[i].Label, x, y, font, labelPaint, SKTextAlign.Left);
            canvas.DrawTextWithFallback(display.Overlay[i].Value, bounds.Right - pad, y, font, valuePaint, SKTextAlign.Right);
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
                TextRenderHelper.BuildSparklinePaths(area, samples, lo, hi, out _sparkLine, out _sparkFill);
            }
        }

        if (_sparkLine is null || _sparkFill is null) return;

        using var fillPaint = new SKPaint { Color = accent.WithAlpha(40), Style = SKPaintStyle.Fill, IsAntialias = true };
        canvas.DrawPath(_sparkFill, fillPaint);
        using var linePaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
        canvas.DrawPath(_sparkLine, linePaint);
    }

    private static void DrawMetricCard(SKCanvas canvas, float cx, float topY, string label, string value, float valSize, float lblSize, SKColor accent)
    {
        var valFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, valSize);
        using var valPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        valFont.MeasureText(value, out var valBounds, valPaint);
        float valY = topY + valSize * 0.85f;
        canvas.DrawTextWithFallback(value, cx - valBounds.Width / 2f, valY, valFont, valPaint);

        var lblFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, lblSize);
        using var lblPaint = new SKPaint { Color = accent, IsAntialias = true };
        lblFont.MeasureText(label, out var lblBounds, lblPaint);
        float lblY = valY + lblSize + 4f;
        canvas.DrawTextWithFallback(label, cx - lblBounds.Width / 2f, lblY, lblFont, lblPaint);
    }
}

