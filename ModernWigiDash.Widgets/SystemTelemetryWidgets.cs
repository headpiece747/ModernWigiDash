using System.Globalization;
using ModernWigiDash.Sdk;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("hardware_monitor", "Hardware Monitor", Category = "System Monitoring")]
public class HardwareMonitorWidget : ModernWidgetBase
{
    public override SKSize DefaultSize => GridSizePreset.Size2x2.ToSize();

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
        if (!ReferenceEquals(snapshot, _lastMatchSnapshot) || _lastMatchLabel != SensorLabel)
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
        SKColor accent = ColorOf(AccentColorHex, new SKColor(255, 205, 133));
        SKColor text = ColorOf(TextColorHex, SKColors.White);

        // The store owns the staleness decision; a stale or disconnected
        // snapshot renders the unavailable state instead of frozen data.
        SensorSnapshotDto? snapshot = LhmSensorStore.TryReadFresh();
        if (snapshot is null || !snapshot.IsConnected)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "No sensor data", "Start LibreHardwareService to read hardware sensors", text);
            return;
        }

        if (string.IsNullOrWhiteSpace(SensorLabel))
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "Select a sensor", "Open Settings and pick a sensor reading", text);
            return;
        }

        SensorReadingDto? reading = MatchReading(snapshot);
        if (reading is null)
        {
            TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, "Sensor not found", $"{SensorLabel} is not currently available", text);
            return;
        }

        float value = (float)reading.Value;
        float max = ResolveMax(reading, value);
        int decimals = Math.Clamp((int)MathF.Round(Decimals), 0, 3);

        string label = string.IsNullOrWhiteSpace(DisplayLabel) ? reading.Label : DisplayLabel;
        string unit = string.IsNullOrWhiteSpace(Unit) ? reading.Unit : Unit;

        switch (SystemTelemetryDisplayModeParser.Parse(DisplayMode))
        {
            case SystemTelemetryDisplayMode.Bar:
                RenderBar(canvas, bounds, label, value, max, unit, decimals, accent, text);
                break;
            case SystemTelemetryDisplayMode.Value:
                RenderValue(canvas, bounds, label, value, unit, decimals, text);
                break;
            case SystemTelemetryDisplayMode.Graph:
                RenderGraph(canvas, bounds, label, value, unit, decimals, accent, text, reading);
                break;
            default:
                RenderGauge(canvas, bounds, label, value, max, unit, decimals, accent, text);
                break;
        }
    }

    /// <summary>
    /// The gauge/bar maximum: the sensor's recorded peak when Auto Scale is on,
    /// else the manual <see cref="MaxValue"/>. Falls back to a value-derived
    /// floor so a zero/negative max can never produce a division-by-zero gauge.
    /// </summary>
    internal float ResolveMax(SensorReadingDto reading, float value)
    {
        if (AutoScale)
        {
            double reference = reading.Max;
            reference = Math.Max(reference, value);
            return reference > 0 ? (float)reference : Math.Max(1f, value * 1.2f);
        }

        return MaxValue > 0 ? MaxValue : Math.Max(1f, value * 1.2f);
    }

    private static void DrawHeader(SKCanvas canvas, SKRect bounds, string label, float pad, SKColor text)
    {
        var headerFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 22f);
        using var headerPaint = new SKPaint { Color = text, IsAntialias = true };
        canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(label, headerFont, bounds.Width - pad * 2f), bounds.Left + pad, bounds.Top + pad + 24f, headerFont, headerPaint);
    }

    /// <summary>
    /// The value progress fraction clamped into 0..1 (shared by the gauge and
    /// bar tracks). A non-positive max can never divide by zero.
    /// </summary>
    internal static float GaugeFraction(float value, float max)
        => Math.Clamp(value / Math.Max(1f, max), 0f, 1f);

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
        float value,
        int decimals,
        float anchorX,
        float baselineAnchor,
        float baselineFromValue,
        float valFontSize,
        SKColor valueColor,
        SKColor unitColor,
        string unit,
        float unitFontSize,
        float unitOffset,
        bool rightAligned = false)
    {
        // The formatted value is memoized per (value bits, decimals): the
        // reading updates ~1×/s, so identical inputs render the cached string
        // (bit-exact keying keeps -0.0 distinct from 0.0).
        int valueBits = BitConverter.SingleToInt32Bits(value);
        if (valueBits != _lastValueBits || decimals != _lastValueDecimals)
        {
            _lastValueBits = valueBits;
            _lastValueDecimals = decimals;
            _lastValueText = value.ToString(ValueFormats[decimals], CultureInfo.InvariantCulture);
        }
        string valStr = _lastValueText;

        var valFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, valFontSize);
        using var valPaint = new SKPaint { Color = valueColor, IsAntialias = true };
        valFont.MeasureText(valStr, out var valBounds, valPaint);

        float valueX = rightAligned ? anchorX - valBounds.Width : anchorX - valBounds.Width / 2f;
        float baselineY = baselineAnchor + valBounds.Height * baselineFromValue;
        canvas.DrawTextWithFallback(valStr, valueX, baselineY, valFont, valPaint);

        if (!string.IsNullOrWhiteSpace(unit))
        {
            var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, unitFontSize);
            using var unitPaint = new SKPaint { Color = unitColor, IsAntialias = true };
            canvas.DrawTextWithFallback(unit, valueX + valBounds.Width + unitOffset, baselineY, unitFont, unitPaint);
        }
    }

    // The hero-value format cache: one slot per widget, keyed bit-exactly on
    // the value and the decimal count. ValueFormats precomputes the "F{n}"
    // format strings (decimals is clamped to 0..3 before this is called).
    private int _lastValueBits = int.MinValue;
    private int _lastValueDecimals = -1;
    private string _lastValueText = "";
    private static readonly string[] ValueFormats = ["F0", "F1", "F2", "F3"];

    private void RenderGauge(SKCanvas canvas, SKRect bounds, string label, float value, float max, string unit, int decimals, SKColor accent, SKColor text)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, label, pad, text);

        float gaugeSize = Math.Min(bounds.Width * 0.42f, bounds.Height - 48f);
        float cx = bounds.MidX;
        float cy = bounds.MidY + 10f;
        float radius = (gaugeSize / 2f) - 10f;
        var arcBounds = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);

        using var trackPaint = new SKPaint { Color = text.WithAlpha(20), Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        canvas.DrawArc(arcBounds, 135f, 270f, false, trackPaint);

        float progress = GaugeFraction(value, max);
        using var progressPaint = new SKPaint { Color = accent, Style = SKPaintStyle.Stroke, StrokeWidth = 12f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
        canvas.DrawArc(arcBounds, 135f, 270f * progress, false, progressPaint);

        DrawHeroValue(canvas, value, decimals, cx, cy, 1f / 3f, gaugeSize * 0.2f, text, text.WithAlpha(180), unit, 11f, 4f);
    }

    private void RenderBar(SKCanvas canvas, SKRect bounds, string label, float value, float max, string unit, int decimals, SKColor accent, SKColor text)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, label, pad, text);

        DrawHeroValue(canvas, value, decimals, bounds.MidX, bounds.MidY + 4f, 0f,
            Math.Clamp(bounds.Height * 0.22f, 22f, 48f), text, text.WithAlpha(180), unit, 12f, 5f);

        var barRect = new SKRect(bounds.Left + pad, bounds.MidY + 20f, bounds.Right - pad, bounds.MidY + 32f);
        float trackRadius = barRect.Height / 2f;

        using var trackPaint = new SKPaint { Color = text.WithAlpha(20), IsAntialias = true };
        canvas.DrawRoundRect(barRect, trackRadius, trackRadius, trackPaint);

        float progress = GaugeFraction(value, max);
        float progressWidth = Math.Max(barRect.Height, barRect.Width * progress);
        var progressRect = new SKRect(barRect.Left, barRect.Top, barRect.Left + progressWidth, barRect.Bottom);
        float progressRadius = Math.Min(trackRadius, progressWidth / 2f);
        using var progressPaint = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawRoundRect(progressRect, progressRadius, progressRadius, progressPaint);
    }

    private void RenderValue(SKCanvas canvas, SKRect bounds, string label, float value, string unit, int decimals, SKColor text)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, label, pad, text);

        DrawHeroValue(canvas, value, decimals, bounds.MidX, bounds.MidY + 4f, 0f,
            Math.Min(bounds.Width * 0.22f, bounds.Height * 0.42f), text, text.WithAlpha(180), unit, 14f, 6f);
    }

    private void RenderGraph(SKCanvas canvas, SKRect bounds, string label, float value, string unit, int decimals, SKColor accent, SKColor text, SensorReadingDto reading)
    {
        float pad = 16f;
        DrawHeader(canvas, bounds, label, pad, text);

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

            TextRenderHelper.DrawSparkline(canvas, area, samples, lo, hi, accent);
        }

        const float valFontSize = 22f;
        DrawHeroValue(canvas, value, decimals, area.Right, area.Top + valFontSize, 0f,
            valFontSize, accent, text.WithAlpha(180), unit, 11f, 4f, rightAligned: true);
    }
}

