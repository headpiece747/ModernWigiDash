namespace ModernWigiDash.Widgets;

/// <summary>
/// The Hardware Monitor widget: renders one live LibreHardwareService sensor
/// reading through the SystemTelemetryPresentation display rules, in gauge,
/// bar, value, or sparkline-graph mode. The pixel layout lives in the shared
/// <see cref="TelemetryReadingRenderer"/> (the HWiNFO widget draws the same
/// modes); this widget owns the data source, gates, and property surface.
/// </summary>
[WidgetMetadata("hardware_monitor", "Hardware Monitor", Category = "System Monitoring")]
public class HardwareMonitorWidget : ModernWidgetBase
{
    /// <summary>The "Sensor": the live sensor reading label selected from LibreHardwareService.</summary>
    [WidgetProperty("Sensor", WidgetPropertyType.SensorSelector, "Select a live sensor reading from LibreHardwareService", "")]
    public string SensorLabel { get; set; } = "";

    /// <summary>The "Display Label": overrides the label shown on the widget (empty = the sensor's name).</summary>
    [WidgetProperty("Display Label", WidgetPropertyType.Text, "Override the label shown on the widget (leave empty to use the sensor name)", "")]
    public string DisplayLabel { get; set; } = "";

    /// <summary>The "Unit": overrides the unit shown (empty = the sensor's unit).</summary>
    [WidgetProperty("Unit", WidgetPropertyType.Text, "Override the unit (leave empty to use the sensor's)", "")]
    public string Unit { get; set; } = "";

    /// <summary>The "Display Mode": how the reading is visualized (Gauge, Bar, Value, or Graph).</summary>
    [WidgetProperty("Display Mode", WidgetPropertyType.Choice, "How to visualize the reading", "Gauge", "Gauge", "Bar", "Value", "Graph")]
    public string DisplayMode { get; set; } = "Gauge";

    /// <summary>The "Auto Scale" toggle: scale the gauge/bar to the maximum recorded by the sensor.</summary>
    [WidgetProperty("Auto Scale", WidgetPropertyType.Boolean, "Scale the gauge/bar to the maximum recorded by the sensor", true)]
    public bool AutoScale { get; set; } = true;

    /// <summary>The "Max Value": the manual gauge/bar maximum when Auto Scale is off.</summary>
    [WidgetProperty("Max Value", WidgetPropertyType.Number, "Manual gauge/bar maximum when Auto Scale is off", 100f)]
    public float MaxValue { get; set; } = 100f;

    /// <summary>The "Decimals": the number of decimal places the hero value shows.</summary>
    [WidgetProperty("Decimals", WidgetPropertyType.Number, "Number of decimal places shown", 1f)]
    public float Decimals { get; set; } = 1f;

    /// <summary>The "Accent Color": the primary accent color (gauge progress, graph).</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Text Color": the header, label, and value color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Header, label, and value color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    private readonly TelemetryReadingRenderer _renderer = new();

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
    internal int HistoryCountForTest => _renderer.HistoryCountForTest;

    /// <summary>
    /// Draws the reading in its display mode, or the unavailable placeholder:
    /// the store's freshness gate first, then the sensor-selected and
    /// sensor-absent placeholders, then the mode dispatch.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="bounds">The widget's bounds in canvas coordinates.</param>
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
        // format, progress) live in the presentation module; the renderer lays
        // them out.
        var display = SystemTelemetryPresentation.Build(
            reading,
            value: (float)reading.Value,
            displayLabelOverride: DisplayLabel,
            unitOverride: Unit,
            displayMode: DisplayMode,
            autoScale: AutoScale,
            maxValue: MaxValue,
            decimals: Decimals);

        _renderer.Render(canvas, bounds, display, (float)reading.Value, reading.Min, reading.Max, accent, text);
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, SKColor text)
        => _renderer.DrawPlaceholder(canvas, bounds, display.PlaceholderTitle, display.PlaceholderSubtitle, text);

    /// <summary>Disposes the shared renderer's paints and sparkline paths.</summary>
    public override ValueTask DisposeAsync()
    {
        _renderer.Dispose();
        return base.DisposeAsync();
    }
}
