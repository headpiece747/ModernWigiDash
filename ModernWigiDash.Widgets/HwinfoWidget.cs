using ModernWigiDash.Hardware.Service;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The dedicated HWiNFO sensor widget (ADR-0022): reads one sensor through the
/// vendor WigiDash service's <see cref="ISensorValues"/> facet. Distinct from the
/// Hardware Monitor widget (which reads LibreHardwareService shared memory);
/// placing one vs. the other IS the source choice. The sensor is picked by name
/// in the inspector (<see cref="IWidgetPropertyOptionsProvider"/>, the vendor's
/// own sensor dropdown), and the reading refreshes on a short throttle so the
/// 30 FPS render tick never becomes a WCF call per frame. It draws with the same
/// Gauge/Bar/Value/Graph display modes as the Hardware Monitor widget (the shared
/// <see cref="TelemetryReadingRenderer"/>); the vendor reports a single value per
/// sensor (no min/max), so auto-scale uses the observed history maximum. Degrades
/// to the house placeholder when the vendor service is absent (ADR-0017 image).
/// </summary>
[WidgetMetadata("hwinfo", "HWiNFO Sensor", Category = "System Monitoring")]
public sealed class HwinfoWidget : ModernWidgetBase, IWidgetPropertyOptionsProvider
{
    /// <summary>How long a fetched sensor list is trusted before a refetch (the list is stable).</summary>
    private const long SensorListRefreshMs = 5000;

    /// <summary>Minimum gap between vendor reads; matches the vendor's own ~1 Hz display cadence.</summary>
    private const long ReadingRefreshMs = 500;

    /// <summary>The "Sensor": which vendor sensor to display, keyed by reading type + ids.</summary>
    [WidgetProperty("Sensor", WidgetPropertyType.Choice, "HWiNFO sensor exposed by the vendor service", "")]
    public string SensorKey { get; set; } = "";

    /// <summary>The "Display Label": overrides the label shown on the widget.</summary>
    [WidgetProperty("Display Label", WidgetPropertyType.Text, "Override the label (leave empty to use the sensor name)", "")]
    public string DisplayLabel { get; set; } = "";

    /// <summary>The "Unit": overrides the unit shown.</summary>
    [WidgetProperty("Unit", WidgetPropertyType.Text, "Override the unit (leave empty to use the sensor's)", "")]
    public string Unit { get; set; } = "";

    /// <summary>The "Display Mode": how the reading is visualized (Gauge, Bar, Value, or Graph).</summary>
    [WidgetProperty("Display Mode", WidgetPropertyType.Choice, "How to visualize the reading", "Gauge", "Gauge", "Bar", "Value", "Graph")]
    public string DisplayMode { get; set; } = "Gauge";

    /// <summary>The "Auto Scale" toggle: scale the gauge/bar to the highest reading observed.</summary>
    [WidgetProperty("Auto Scale", WidgetPropertyType.Boolean, "Scale the gauge/bar to the highest reading observed", true)]
    public bool AutoScale { get; set; } = true;

    /// <summary>The "Max Value": the manual gauge/bar maximum when Auto Scale is off.</summary>
    [WidgetProperty("Max Value", WidgetPropertyType.Number, "Manual gauge/bar maximum when Auto Scale is off", 100f)]
    public float MaxValue { get; set; } = 100f;

    /// <summary>The "Decimals": the number of decimal places the hero value shows.</summary>
    [WidgetProperty("Decimals", WidgetPropertyType.Number, "Number of decimal places shown", 1f)]
    public float Decimals { get; set; } = 1f;

    /// <summary>The "Accent Color": the primary accent color.</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Text Color": the header, label, and value color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Header, label, and value color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    private VendorSensorItem[]? _sensorList;
    private long _sensorListAt;
    private VendorSensorItem? _sensor;
    private double _lastValue;
    private bool _hasReading;
    private long _readingAt;
    private string? _readingKey;

    private readonly TelemetryReadingRenderer _renderer = new();

    /// <summary>Internal test accessor: how many history samples are buffered.</summary>
    internal int HistoryCountForTest => _renderer.HistoryCountForTest;

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var client = VendorService.Instance;
        if (client == null)
        {
            DrawPlaceholder(canvas, bounds, serviceReady: false);
            return;
        }

        // No readiness gate here: RefreshReading's list refresh (every 5 s)
        // drives the client's self-heal, so a vendor service that starts or
        // restarts after the app is picked up without a relaunch.
        RefreshReading(client);

        if (!_hasReading || _sensor == null)
        {
            DrawPlaceholder(canvas, bounds, client.Sensors.IsReady);
            return;
        }

        // The vendor reports a single value per sensor (no min/max), so
        // auto-scale uses the observed history maximum as its reference; a
        // manual Max Value applies when Auto Scale is off.
        var display = SystemTelemetryPresentation.Build(
            _sensor.Name,
            _sensor.Unit ?? string.Empty,
            _renderer.HistoryMax,
            (float)_lastValue,
            displayLabelOverride: DisplayLabel,
            unitOverride: Unit,
            displayMode: DisplayMode,
            autoScale: AutoScale,
            maxValue: MaxValue,
            decimals: Decimals);

        _renderer.Render(
            canvas, bounds, display, (float)_lastValue, 0, 0,
            ColorOf(AccentColorHex, WidgetPalette.Accent),
            ColorOf(TextColorHex, SKColors.White));
    }

    /// <summary>
    /// The sensor dropdown: the vendor's live sensor list, keyed by
    /// reading type + ids so the persisted pick survives a service restart.
    /// </summary>
    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (!string.Equals(propertyName, nameof(SensorKey), StringComparison.Ordinal))
            return [];

        var sensors = VendorService.Instance?.Sensors.GetSensorList();
        if (sensors == null)
            return [];

        return sensors
            .Select(s =>
            {
                var source = string.IsNullOrWhiteSpace(s.Type) ? null : s.Type;
                var label = source == null ? s.Name : $"{s.Name} ({source})";
                // The vendor's Name/Type are untrusted and only per-string quota
                // bounded (8 KB); bound the UI label too.
                if (label.Length > 128)
                {
                    label = label[..128];
                }

                return new WidgetPropertyOption(ComposeKey(s.ReadingType, s.SensorId1, s.SensorId2), label);
            })
            .ToArray();
    }

    private void RefreshReading(WigiDashServiceClient client)
    {
        var now = Environment.TickCount64;

        // Retry fast until the first list lands (the client serves it from a
        // background cache and schedules the fetch, so a miss is cheap); once a
        // list is held, settle to the 5 s cadence.
        long listCadence = _sensorList is { Length: > 0 } ? SensorListRefreshMs : 250;
        if (now - _sensorListAt >= listCadence)
        {
            _sensorList = client.Sensors.GetSensorList()?.ToArray();
            _sensorListAt = now;
        }

        if (_sensorList == null || _sensorList.Length == 0)
        {
            _sensor = null;
            _hasReading = false;
            return;
        }

        // An empty pick falls back to the first sensor, so a freshly placed
        // widget shows a live reading instead of an empty placeholder.
        var key = SensorKey;
        if (!TryParseKey(key, out int readingType, out int sensorId1, out int sensorId2))
        {
            _sensor = _sensorList[0];
            readingType = _sensor.ReadingType;
            sensorId1 = _sensor.SensorId1;
            sensorId2 = _sensor.SensorId2;
        }
        else
        {
            _sensor = Array.Find(_sensorList, s =>
                s.ReadingType == readingType && s.SensorId1 == sensorId1 && s.SensorId2 == sensorId2);
            if (_sensor == null)
            {
                _hasReading = false;
                return;
            }
        }

        if (now - _readingAt < ReadingRefreshMs && string.Equals(_readingKey, key, StringComparison.Ordinal))
            return;

        _readingAt = now;
        _readingKey = key;

        var reading = client.Sensors.GetSensorValue(readingType, sensorId1, sensorId2);
        if (reading == null || !reading.IsValid)
        {
            _hasReading = false;
            return;
        }

        _lastValue = reading.Value;
        _hasReading = true;
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds, bool serviceReady)
        => _renderer.DrawPlaceholder(
            canvas,
            bounds,
            "HWiNFO",
            serviceReady ? "No sensor data" : "Vendor service not connected",
            ColorOf("#9CA3AF", SKColors.Gray));

    /// <summary>The stable key for a vendor sensor: reading type + both ids.</summary>
    internal static string ComposeKey(int readingType, int sensorId1, int sensorId2)
        => $"{readingType}:{sensorId1}:{sensorId2}";

    /// <summary>Parses a key produced by <see cref="ComposeKey"/>; false for an empty/garbage value.</summary>
    internal static bool TryParseKey(string? key, out int readingType, out int sensorId1, out int sensorId2)
    {
        readingType = 0;
        sensorId1 = 0;
        sensorId2 = 0;
        if (string.IsNullOrEmpty(key))
            return false;

        var parts = key.Split(':');
        return parts.Length == 3
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out readingType)
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sensorId1)
            && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out sensorId2);
    }

    /// <inheritdoc />
    public override ValueTask DisposeAsync()
    {
        _renderer.Dispose();
        return base.DisposeAsync();
    }
}
