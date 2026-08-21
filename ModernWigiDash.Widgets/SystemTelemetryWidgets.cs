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
    private bool _disposed;

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
    // (the history appends a sample every frame, so the geometry is never
    // stable — the paths themselves must not be reallocated either).
    private SKPath? _sparkLinePath;
    private SKPath? _sparkFillPath;

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

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, SKColor text)
        => TextRenderHelper.DrawTitleSubtitlePlaceholder(canvas, bounds, display.PlaceholderTitle, display.PlaceholderSubtitle, text, _placeholderTitlePaint, _placeholderSubPaint);

    private void DrawHeader(SKCanvas canvas, SKRect bounds, string label, float pad, SKColor text)
    {
        var headerFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, 22f);
        _headerPaint.Color = text;
        canvas.DrawTextWithFallback(TextRenderHelper.TruncateText(label, headerFont, bounds.Width - pad * 2f), bounds.Left + pad, bounds.Top + pad + 24f, headerFont, _headerPaint);
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
        _valuePaint.Color = valueColor;
        valFont.MeasureText(valStr, out var valBounds, _valuePaint);

        float valueX = rightAligned ? anchorX - valBounds.Width : anchorX - valBounds.Width / 2f;
        float baselineY = baselineAnchor + valBounds.Height * baselineFromValue;
        canvas.DrawTextWithFallback(valStr, valueX, baselineY, valFont, _valuePaint);

        if (!string.IsNullOrWhiteSpace(display.Unit))
        {
            var unitFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, unitFontSize);
            _unitPaint.Color = unitColor;
            canvas.DrawTextWithFallback(display.Unit, valueX + valBounds.Width + unitOffset, baselineY, unitFont, _unitPaint);
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

        _gaugeTrackPaint.Color = text.WithAlpha(20);
        canvas.DrawArc(arcBounds, 135f, 270f, false, _gaugeTrackPaint);

        float progress = display.Progress;
        _gaugeProgressPaint.Color = accent;
        canvas.DrawArc(arcBounds, 135f, 270f * progress, false, _gaugeProgressPaint);

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

            // The caller-owned paths are rewound per frame (the sample history
            // changes every frame, so the geometry is never stable — the paths
            // themselves are not reallocated either), and the hoisted paints
            // are re-colored per frame.
            _sparkFillPath ??= new SKPath();
            _sparkLinePath ??= new SKPath();
            SparklineRenderer.RebuildSparklinePaths(area, samples, lo, hi, _sparkLinePath, _sparkFillPath);
            _sparkFillPaint.Color = accent.WithAlpha(40);
            canvas.DrawPath(_sparkFillPath, _sparkFillPaint);
            _sparkLinePaint.Color = accent;
            canvas.DrawPath(_sparkLinePath, _sparkLinePaint);
        }

        const float valFontSize = 22f;
        DrawHeroValue(canvas, display, area.Right, area.Top + valFontSize, 0f,
            valFontSize, accent, text.WithAlpha(180), 11f, 4f, rightAligned: true);
    }

    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
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
        return base.DisposeAsync();
    }
}

