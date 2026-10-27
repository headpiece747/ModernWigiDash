namespace ModernWigiDash.Widgets;

/// <summary>
/// The Hardware Monitor widget: renders one live LibreHardwareService sensor
/// reading through the SystemTelemetryPresentation display rules, in gauge,
/// bar, value, or sparkline-graph mode. The pixel layout lives in the shared
/// <see cref="TelemetryReadingRenderer"/> (the HWiNFO widget draws the same
/// modes); this widget owns the data source, gates, and property surface.
/// </summary>
[WidgetMetadata("hardware_monitor", "Hardware Monitor", Category = "System Monitoring")]
public class HardwareMonitorWidget : TelemetrySensorWidgetBase
{
    /// <summary>The "Sensor": the live sensor reading label selected from LibreHardwareService.</summary>
    [WidgetProperty("Sensor", WidgetPropertyType.SensorSelector, "Select a live sensor reading from LibreHardwareService", "")]
    public string SensorLabel { get; set; } = "";

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

    /// <summary>
    /// Draws the reading in its display mode, or the unavailable placeholder:
    /// the store's freshness gate first, then the sensor-selected and
    /// sensor-absent placeholders, then the mode dispatch.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="bounds">The widget's bounds in canvas coordinates.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var (accent, text) = ResolveColors();

        // The store owns the staleness decision; a stale or disconnected
        // snapshot renders the unavailable state instead of frozen data.
        SensorSnapshotDto? snapshot = (Context as IWidgetTelemetryContext)?.TryReadSensors() ?? LhmSensorStore.TryReadFresh();
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

        Renderer.Render(canvas, bounds, display, (float)reading.Value, reading.Min, reading.Max, accent, text);
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds, SystemTelemetryDisplay display, SKColor text)
        => Renderer.DrawPlaceholder(canvas, bounds, display.PlaceholderTitle, display.PlaceholderSubtitle, text);
}
