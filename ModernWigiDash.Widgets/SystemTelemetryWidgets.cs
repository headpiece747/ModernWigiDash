using ModernWigiDash.Sdk;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("hardware_monitor", "Hardware Monitor", Category = "System Monitoring")]
public class HardwareMonitorWidget : ModernWidgetBase
{
    [WidgetProperty("Sensor", WidgetPropertyType.SensorSelector, "Select a live sensor reading from LibreHardwareService", "")]
    public string SensorLabel { get; set; } = "";

    [WidgetProperty("Display Label", WidgetPropertyType.Text, "Override the label shown on the widget (leave empty to use the sensor name)", "")]
    public string DisplayLabel { get; set; } = "";

    [WidgetProperty("Unit", WidgetPropertyType.Text, "Override the unit (leave empty to use the sensor's)", "")]
    public string Unit { get; set; } = "";

    [WidgetProperty("Display Mode", WidgetPropertyType.Choice, "How to visualize the reading", "Gauge", "Gauge", "Bar", "Value", "Graph")]
    public string DisplayMode { get; set; } = "Gauge";

    [WidgetProperty("Auto Scale", WidgetPropertyType.Boolean, "Scale the gauge/bar to the maximum recorded by the sensor", true)]
    public bool AutoScale { get; set; } = true;

    [WidgetProperty("Max Value", WidgetPropertyType.Number, "Manual gauge/bar maximum when Auto Scale is off", 100f)]
    public float MaxValue { get; set; } = 100f;

    [WidgetProperty("Decimals", WidgetPropertyType.Number, "Number of decimal places shown", 1f)]
    public float Decimals { get; set; } = 1f;

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Header, label, and value color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    private readonly Queue<float> _history = new();
    private const int HistoryCapacity = 96;

    // The SensorLabel→reading match was a linear scan per frame; the match is
    // cached keyed by (snapshot identity, label) — a new snapshot (~1/s) or a
    // label change re-scans, the frames in between reuse the result.
    private SensorSnapshotDto? _lastMatchSnapshot;
    private string _lastMatchLabel = "";
    private SensorReadingDto? _matchedReading;

    private SensorReadingDto? MatchReading(SensorSnapshotDto snapshot)
    {
        if (!ReferenceEquals(snapshot, _lastMatchSnapshot) || !string.Equals(_lastMatchLabel, SensorLabel, StringComparison.Ordinal))
        {
            _lastMatchSnapshot = snapshot;
            _lastMatchLabel = SensorLabel;
            _matchedReading = snapshot.Readings.FirstOrDefault(r => string.Equals(r.Label, SensorLabel, StringComparison.OrdinalIgnoreCase));
        }
        return _matchedReading;
    }

    /// <summary>Internal test accessor: how many history samples are buffered.</summary>
    internal int HistoryCountForTest => _history.Count;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor accent = ColorOf(AccentColorHex, WidgetPalette.Accent);
        SKColor text = ColorOf(TextColorHex, SKColors.White);

        // The store owns the staleness decision; a stale or disconnected
        // snapshot renders the unavailable state instead of frozen data.
        SensorSnapshotDto? snapshot = LhmSensorStore.TryReadFresh();
        if (snapshot is null || !snapshot.IsConnected)
        {
            DrawPlaceholder(canvas, bounds, SystemTelemetryPresentation.NoSensorData(), text);
            return;
        }

        if (string.IsNullOrWhiteSpace(SensorLabel))
        {
            DrawPlaceholder(canvas, bounds, SystemTelemetryPresentation.NoSensorSelected(), text);
            return;
        }

        SensorReadingDto? reading = MatchReading(snapshot);
        if (reading is null)
        {
            DrawPlaceholder(canvas, bounds, SystemTelemetryPresentation.SensorNotPresent(SensorLabel), text);
            return;
        }

        // The display rules (label/unit resolution, mode fallback, value
        // format, progress) live in the presentation module; the render
        // methods below are thin adapters that lay the display out.
        var display = SystemTelemetryPresentation.Build(
            reading,
            value: (float)reading.Value,
            displayLabelOverride: DisplayLabel,
            unitOverride: Unit,
            displayMode: DisplayMode,
            autoScale: AutoScale,
            maxValue: MaxValue,
            decimals: Decimals);

        switch (display.Mode)
        {
            case SystemTelemetryDisplayMode.Bar:
                RenderBar(canvas, bounds, display, accent, text);
                break;
            case SystemTelemetryDisplayMode.Value:
                RenderValue(canvas, bounds, display, text);
                break;
            case SystemTelemetryDisplayMode.Graph:
                RenderGraph(canvas, bounds, display, (float)reading.Value, accent, text, reading);
                break;
            default:
                RenderGauge(canvas, bounds, display, accent, text);
                break;
        }
    }

    private static void DrawPlaceholder(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, SKColor text)
        => TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, display.PlaceholderTitle, display.PlaceholderSubtitle, text);

    private static void DrawHeader(SKCanvas canvas, SKRect bounds, string label, float pad, SKColor text)
    {
        var headerFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 22f);
        using var headerPaint = new SKPaint { Color = text, IsAntialias = true };
        canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(label, headerFont, bounds.Width - pad * 2f), bounds.Left + pad, bounds.Top + pad + 24f, headerFont, headerPaint);
    }

    /// <summary>
    /// Draws the big hero value with its trailing unit — the "value + unit"
    /// block shared by the four display modes. Per-mode spacing stays at the
    /// call sites: value font size, baseline anchor, unit font size, and the
    /// unit's pixel offset from the value (the +4/+5/+6 deltas are pixel
    /// behavior, not duplication).
    /// </summary>
    /// <param name="anchorX">The value's horizontal anchor: its center, or its
    /// right edge when <paramref name="rightAligned"/> is set.</param>
    /// <param name="baselineAnchor">Baseline before the value's own height
    /// contribution; Gauge adds 1/3 of the measured height to sit the value on
    /// its own metrics, the other modes use 0 for a fixed baseline.</param>
    /// <param name="baselineFromValue">Fraction of the measured value height
    /// added to <paramref name="baselineAnchor"/> for the baseline.</param>
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
        bool rightAligned = false)
    {
        string valStr = display.ValueText;

        var valFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, valFontSize);
        using var valPaint = new SKPaint { Color = valueColor, IsAntialias = true };
        valFont.MeasureText(valStr, out var valBounds, valPaint);

        float valueX = rightAligned ? anchorX - valBounds.Width : anchorX - valBounds.Width / 2f;
        float baselineY = baselineAnchor + valBounds.Height * baselineFromValue;
        canvas.DrawTextWithFallback(valStr, valueX, baselineY, valFont, valPaint);

        if (!string.IsNullOrWhiteSpace(display.Unit))
        {
            var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, unitFontSize);
            using var unitPaint = new SKPaint { Color = unitColor, IsAntialias = true };
            canvas.DrawTextWithFallback(display.Unit, valueX + valBounds.Width + unitOffset, baselineY, unitFont, unitPaint);
        }
    }

    private void RenderGauge(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, SKColor accent, SKColor text)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, display.Label, pad, text);

        float gaugeSize = Math.Min(bounds.Width * 0.42f, bounds.Height - 48f);
        float cx = bounds.MidX;
        float cy = bounds.MidY + 10f;
        float radius = (gaugeSize / 2f) - 10f;
        var arcBounds = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);

        using var trackPaint = new SKPaint { Color = text.WithAlpha(20), Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        canvas.DrawArc(arcBounds, 135f, 270f, false, trackPaint);

        float progress = display.Progress;
        using var progressPaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        canvas.DrawArc(arcBounds, 135f, 270f * progress, false, progressPaint);

        DrawHeroValue(canvas, display, cx, cy, 1f / 3f, gaugeSize * 0.2f, text, text.WithAlpha(180), 11f, 4f);
    }

    private void RenderBar(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, SKColor accent, SKColor text)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, display.Label, pad, text);

        DrawHeroValue(canvas, display, bounds.MidX, bounds.MidY + 4f, 0f,
            Math.Clamp(bounds.Height * 0.22f, 22f, 48f), text, text.WithAlpha(180), 12f, 5f);

        var barRect = new SKRect(bounds.Left + pad, bounds.MidY + 20f, bounds.Right - pad, bounds.MidY + 32f);
        float trackRadius = barRect.Height / 2f;

        using var trackPaint = new SKPaint { Color = text.WithAlpha(20), IsAntialias = true };
        canvas.DrawRoundRect(barRect, trackRadius, trackRadius, trackPaint);

        float progress = display.Progress;
        float progressWidth = Math.Max(barRect.Height, barRect.Width * progress);
        var progressRect = new SKRect(barRect.Left, barRect.Top, barRect.Left + progressWidth, barRect.Bottom);
        float progressRadius = Math.Min(trackRadius, progressWidth / 2f);
        using var progressPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawRoundRect(progressRect, progressRadius, progressRadius, progressPaint);
    }

    private void RenderValue(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, SKColor text)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, display.Label, pad, text);

        DrawHeroValue(canvas, display, bounds.MidX, bounds.MidY + 4f, 0f,
            Math.Min(bounds.Width * 0.22f, bounds.Height * 0.42f), text, text.WithAlpha(180), 14f, 6f);
    }

    private void RenderGraph(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, float value, SKColor accent, SKColor text, SensorReadingDto reading)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, display.Label, pad, text);

        // The sparkline is the only consumer of the history buffer, so the
        // sample is appended here — Gauge/Bar/Value frames skip the queue work.
        _history.Enqueue(value);
        while (_history.Count > HistoryCapacity)
        {
            _history.Dequeue();
        }

        float graphTop = bounds.Top + 40f;
        float graphBottom = bounds.Bottom - pad;
        var area = new SKRect(bounds.Left + pad, graphTop, bounds.Right - pad, graphBottom);

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

            float lo = Math.Min(min, (float)reading.Min);
            float hi = Math.Max(max, (float)reading.Max);
            if (hi - lo < 1e-6f)
            {
                lo = value - 1f;
                hi = value + 1f;
            }

            SparklineRenderer.DrawSparkline(canvas, area, samples, lo, hi, accent);
        }

        const float valFontSize = 22f;
        DrawHeroValue(canvas, display, area.Right, area.Top + valFontSize, 0f,
            valFontSize, accent, text.WithAlpha(180), 11f, 4f, rightAligned: true);
    }
}

