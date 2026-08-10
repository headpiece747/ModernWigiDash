using System.Globalization;
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
/// no tracked process, the dashboard renders em-dash placeholders instead of
/// fabricated numbers.
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
        FrameTimeSnapshotRecord? snapshot = FrameTimeStore.TryReadFresh();
        if (snapshot == null || !snapshot.IsAvailable)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "Frame capture unavailable", "Install and run the PresentMon Service", text);
            return;
        }

        if (!snapshot.CaptureHealthy)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "PresentMon capture inactive", "The service is not producing present data", text);
            return;
        }

        if (IsOverlayView)
        {
            RenderOverlayView(canvas, bounds, accent, text, snapshot);
            return;
        }

        if (snapshot.ProcessId <= 0)
        {
            RenderDashView(canvas, bounds, accent, text);
            return;
        }

        RenderTrackedView(canvas, bounds, accent, text, snapshot);
    }

    /// <summary>
    /// No process tracked (desktop / own window foreground): the layout renders
    /// with "—" values — PresentMon has no data to show and its overlay renders
    /// nothing. No fabricated numbers.
    /// </summary>
    private void RenderDashView(SKCanvas canvas, SKRect bounds, SKColor accent, SKColor text)
    {
        float pad = Math.Clamp(bounds.Height * 0.05f, 10f, 22f);
        float heroTop = bounds.Top + pad;
        float heroH = Math.Max(8f, bounds.Height - pad * 2f);

        float fpsFontSize = Math.Clamp(heroH * 0.85f, 24f, 120f);
        var fpsFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize);
        using var fpsPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        canvas.DrawTextWithFallback("—", bounds.Left + pad, heroTop + fpsFontSize * 0.82f, fpsFont, fpsPaint);

        var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize * 0.32f);
        using var unitPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawTextWithFallback("FPS", bounds.Left + pad + fpsFont.MeasureText("—", fpsPaint) + 10f,
            heroTop + fpsFontSize * 0.38f, unitFont, unitPaint);

        if (bounds.Width >= 410f)
        {
            var labelFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 11f);
            using var labelPaint = new SKPaint { Color = accent, IsAntialias = true };
            var valueFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 15f);
            using var valuePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            float cardTop = heroTop + fpsFontSize * 0.82f + 12f;
            float colWidth = (bounds.Width - pad * 2f) / 4f;
            string[] labels = ["1% LOW", "0.1% LOW", "CPU FRAME", "GPU BUSY"];
            for (int i = 0; i < labels.Length; i++)
            {
                float cx = bounds.Left + pad + colWidth * (i + 0.5f);
                float valW = valueFont.MeasureText("—", valuePaint);
                canvas.DrawTextWithFallback("—", cx - valW / 2f, cardTop + 13f, valueFont, valuePaint);
                float lblW = labelFont.MeasureText(labels[i], labelPaint);
                canvas.DrawTextWithFallback(labels[i], cx - lblW / 2f, cardTop + 13f + 20f, labelFont, labelPaint);
            }
        }
    }

    private void RenderTrackedView(SKCanvas canvas, SKRect bounds, SKColor accent, SKColor text, FrameTimeSnapshotRecord snapshot)
    {
        float pad = Math.Clamp(bounds.Height * 0.05f, 10f, 22f);

        bool tiny = bounds.Height < 150f;
        bool showCards = bounds.Width >= 410f;
        bool showSecondRow = bounds.Width >= 520f;
        bool showGraph = bounds.Height >= 150f && snapshot.RecentFrameTimesMs.Count >= 2;
        float graphHeight = showGraph ? bounds.Height * 0.12f : 0f;

        float contentTop = bounds.Top + pad;
        float contentBottom = bounds.Bottom - pad - (showGraph ? graphHeight + 6f : 0f);

        float heroTop = contentTop;
        if (!tiny && ShowProcess && !string.IsNullOrWhiteSpace(snapshot.ProcessName))
        {
            float procSize = Math.Clamp((contentBottom - contentTop) * 0.08f, 10f, 15f);
            var processFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, procSize);
            using var processPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
            string process = TextRenderHelper.TruncateText(snapshot.ProcessName, processFont, bounds.Width - pad * 2f);
            canvas.DrawTextWithFallback(process, bounds.Right - pad - FontHelper.MeasureTextWithFallback(process, processFont), contentTop + procSize, processFont, processPaint);
            heroTop = contentTop + procSize + 6f;
        }

        float heroBottom = showCards ? contentTop + (contentBottom - contentTop) * 0.45f : contentBottom;
        float heroH = Math.Max(8f, heroBottom - heroTop);

        float fpsFontSize = Math.Clamp(heroH * 0.85f, 24f, 120f);
        var fpsFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, fpsFontSize);
        using var fpsPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        RefreshCachedStrings(snapshot);
        string fpsText = _cachedFpsText;
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
        canvas.DrawTextWithFallback(_cachedMsText, unitX, fpsBaseline, msFont, msPaint);

        if (showCards)
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

                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 0.5f, row1Top, "1% LOW", _cachedLow1, metricValSize, metricLblSize, accent);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 1.5f, row1Top, "0.1% LOW", _cachedLow01, metricValSize, metricLblSize, accent);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 2.5f, row1Top, "CPU FRAME", _cachedCpu, metricValSize, metricLblSize, accent);
                DrawMetricCard(canvas, bounds.Left + pad + colWidth * 3.5f, row1Top, "GPU BUSY", _cachedGpu, metricValSize, metricLblSize, accent);

                if (showSecondRow)
                {
                    DrawMetricCard(canvas, bounds.Left + pad + colWidth * 0.5f, row2Top, "DISPLAYED", _cachedDisplayed, metricValSize, metricLblSize, accent);
                    DrawMetricCard(canvas, bounds.Left + pad + colWidth * 1.5f, row2Top, "DROPPED", _cachedDropped, metricValSize, metricLblSize, accent);
                    DrawMetricCard(canvas, bounds.Left + pad + colWidth * 2.5f, row2Top, "GPU TIME", _cachedGpuTime, metricValSize, metricLblSize, accent);
                    DrawMetricCard(canvas, bounds.Left + pad + colWidth * 3.5f, row2Top, "PRESENT MODE", _cachedPresentMode, metricValSize, metricLblSize, accent);
                }
            }
        }

        if (showGraph)
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
    /// own overlay lists, in the project font and the widget's colors. Frame
    /// times derive from the percentile FPS values, matching PresentMon's
    /// 99th/1st %tile stat naming. Lines clip from the bottom as the placement
    /// shrinks.
    /// </summary>
    private void RenderOverlayView(SKCanvas canvas, SKRect bounds, SKColor accent, SKColor text, FrameTimeSnapshotRecord snapshot)
    {
        bool dash = snapshot.ProcessId <= 0;
        float pad = Math.Clamp(bounds.Height * 0.06f, 8f, 20f);
        float fontSize = Math.Clamp(bounds.Height * 0.052f, 10f, 24f);

        var font = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, fontSize);
        using var labelPaint = new SKPaint { Color = text.WithAlpha(180), IsAntialias = true };
        using var valuePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float lineHeight = fontSize * 1.45f;

        int maxLines = bounds.Height < 110f ? 1 : bounds.Height < 150f ? 4 : 9;
        int lines = Math.Min(maxLines, dash ? 1 : 9);

        string F1(double v) => v > 0 ? $"{v:F1} ms" : "—";
        string F0(double v) => v > 0 ? $"{v:F0}" : "—";

        string[] labels =
        [
            "Presented FPS", "Displayed FPS", "99th %tile Frame Time", "1st %tile Frame Time",
            "GPU Busy %", "GPU Time", "CPU Frame Time", "Dropped Frames", "Present Mode",
        ];
        string[] values =
        [
            dash ? "—" : $"{snapshot.Fps:F0}",
            dash ? "—" : F0(snapshot.DisplayedFps),
            dash ? "—" : F1(1000.0 / snapshot.Low1PercentFps),
            dash ? "—" : F1(1000.0 / snapshot.Low01PercentFps),
            dash ? "—" : $"{snapshot.GpuBusyPercent:F0}%",
            dash ? "—" : F1(snapshot.GpuTimeMs),
            dash ? "—" : F1(snapshot.CpuFrameTimeMs),
            dash ? "—" : snapshot.DroppedFrames.ToString(CultureInfo.InvariantCulture),
            dash ? "—" : PresentMonPresentMode.FullName(snapshot.PresentModeId),
        ];

        float x = bounds.Left + pad;
        for (int i = 0; i < lines; i++)
        {
            float y = bounds.Top + pad + (i + 1) * lineHeight;
            canvas.DrawTextWithFallback(labels[i], x, y, font, labelPaint, SKTextAlign.Left);
            canvas.DrawTextWithFallback(values[i], bounds.Right - pad, y, font, valuePaint, SKTextAlign.Right);
        }
    }

    private FrameTimeSnapshotRecord? _lastStringSnapshot;
    private string _cachedFpsText = "";
    private string _cachedMsText = "";
    private string _cachedLow1 = "";
    private string _cachedLow01 = "";
    private string _cachedGpu = "";
    private string _cachedCpu = "";
    private string _cachedDisplayed = "";
    private string _cachedDropped = "";
    private string _cachedGpuTime = "";
    private string _cachedPresentMode = "";

    /// <summary>
    /// Formats the snapshot strings once per snapshot instance (the store swaps
    /// the record ~1/s) instead of per render at 30 FPS.
    /// </summary>
    private void RefreshCachedStrings(FrameTimeSnapshotRecord snapshot)
    {
        if (ReferenceEquals(snapshot, _lastStringSnapshot)) return;
        _lastStringSnapshot = snapshot;
        _cachedFpsText = snapshot.Fps.ToString("F0", CultureInfo.InvariantCulture);
        _cachedMsText = $"{snapshot.FrameTimeMs:F1} ms";
        _cachedLow1 = $"{snapshot.Low1PercentFps:F0} FPS";
        _cachedLow01 = $"{snapshot.Low01PercentFps:F0} FPS";
        _cachedGpu = $"{snapshot.GpuBusyPercent:F0}%";
        _cachedCpu = $"{snapshot.CpuFrameTimeMs:F1} ms";
        _cachedDisplayed = $"{snapshot.DisplayedFps:F0} FPS";
        _cachedDropped = snapshot.DroppedFrames.ToString(CultureInfo.InvariantCulture);
        _cachedGpuTime = $"{snapshot.GpuTimeMs:F1} ms";
        _cachedPresentMode = snapshot.PresentModeId >= 0
            ? PresentMonPresentMode.ShortName(snapshot.PresentModeId)
            : "—";
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

        if (_sparkLine == null || _sparkFill == null) return;

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
