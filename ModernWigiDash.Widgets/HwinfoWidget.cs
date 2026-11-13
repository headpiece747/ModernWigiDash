using ModernWigiDash.Hardware.Service;

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
public sealed class HwinfoWidget : TelemetrySensorWidgetBase, IWidgetPropertyOptionsProvider
{
    /// <summary>The "Sensor": which vendor sensor to display, keyed by reading type + ids.</summary>
    [WidgetProperty("Sensor", WidgetPropertyType.Choice,
        "No HWiNFO sensors available yet. Start the G.SKILL WigiDash service and HWiNFO if they are not running.",
        "", RequiresListedValue = true)]
    public string SensorKey { get; set; } = "";

    // The refresh core (the ADR-0022 image): the cadence, the key codec, the
    // read throttle, and the empty-pick fallback in one module, driven with
    // one call per render tick through the client's ISensorValues facet.
    private readonly VendorSensorRefresh _refresh = new();

    // Tracks whether the last refresh observed live sensors so the inspector
    // rebuilds when the cold cache populates or when the service reconnects
    // after a disconnect.
    private bool _hadSensorList;

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var (accent, text) = ResolveColors();

        var client = ResolveClient();
        if (client == null)
        {
            NotifySensorListChanged(false);
            DrawPlaceholder(canvas, bounds, serviceReady: false);
            return;
        }

        // No readiness gate here: the refresh core's list refresh (every 5 s)
        // drives the client's self-heal, so a vendor service that starts or
        // restarts after the app is picked up without a relaunch.
        var reading = _refresh.Refresh(client.Sensors, SensorKey);
        NotifySensorListChanged(_refresh.HasSensorList);
        if (reading is null)
        {
            DrawPlaceholder(canvas, bounds, client.Sensors.IsReady);
            return;
        }

        // The vendor reports a single value per sensor (no min/max), so
        // auto-scale uses the observed history maximum as its reference; a
        // manual Max Value applies when Auto Scale is off.
        var display = SystemTelemetryPresentation.Build(
            reading.Name,
            reading.Unit,
            Renderer.HistoryMax,
            (float)reading.Value,
            displayLabelOverride: DisplayLabel,
            unitOverride: Unit,
            displayMode: DisplayMode,
            autoScale: AutoScale,
            maxValue: MaxValue,
            decimals: Decimals);

        Renderer.Render(
            canvas, bounds, display, (float)reading.Value, 0, 0,
            accent,
            text);
    }

    /// <summary>
    /// The sensor dropdown: the vendor's live sensor list, keyed by
    /// reading type + ids so the persisted pick survives a service restart.
    /// </summary>
    public IReadOnlyList<WidgetPropertyOption> GetPropertyOptions(string propertyName)
    {
        if (!string.Equals(propertyName, nameof(SensorKey), StringComparison.Ordinal))
            return [];

        var client = ResolveClient();
        var sensors = client?.Sensors.GetSensorList();

        bool hasLiveSensors = sensors is { Count: > 0 };

        var options = new List<WidgetPropertyOption>
        {
            // The refresh core falls back to the first sensor when the key is
            // empty or unparseable (the empty-pick rule). Keep this option
            // present even while the vendor cache is cold or offline so the
            // Sensor dropdown never vanishes in settings.
            new("", "Automatic (first available sensor)") { IsFallback = !hasLiveSensors },
        };

        // A persisted key the current list no longer contains (a service
        // restart, a renamed sensor, an offline cache, or text the old free-form
        // editor allowed) must stay visible and selectable so the user can see
        // what was stored or switch back to Automatic.
        bool pickedKeyParsed = VendorSensorRefresh.TryParseKey(SensorKey, out int pickedType, out int pickedId1, out int pickedId2);
        bool pickedKeyPresent = sensors != null
            && pickedKeyParsed
            && sensors.Any(s => s.ReadingType == pickedType && s.SensorId1 == pickedId1 && s.SensorId2 == pickedId2);
        if (!string.IsNullOrEmpty(SensorKey) && !pickedKeyPresent)
        {
            options.Add(new WidgetPropertyOption(SensorKey, $"{SensorKey} (not available)") { IsFallback = !hasLiveSensors });
        }

        if (sensors != null)
        {
            options.AddRange(sensors.Select(s =>
            {
                var name = s.Name ?? string.Empty;
                var source = string.IsNullOrWhiteSpace(s.Type) ? null : s.Type;
                var label = source == null ? name : $"{name} ({source})";
                // The vendor's Name/Type are untrusted and only per-string quota
                // bounded (8 KB); bound the UI label too.
                if (label.Length > 128)
                {
                    label = label[..128];
                }

                return new WidgetPropertyOption(VendorSensorRefresh.ComposeKey(s.ReadingType, s.SensorId1, s.SensorId2), label);
            }));
        }

        return options;
    }

    private void NotifySensorListChanged(bool hasList)
    {
        if (!hasList)
        {
            // Reset so a service disconnect + reconnect refreshes the dropdown
            // once sensors return, without flashing the inspector on loss.
            _hadSensorList = false;
            return;
        }

        if (_hadSensorList)
        {
            return;
        }

        _hadSensorList = true;
        Context?.RequestInspectorRefresh();
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds, bool serviceReady)
        => Renderer.DrawPlaceholder(
            canvas,
            bounds,
            "HWiNFO",
            serviceReady ? "No sensor data" : "Vendor service not connected",
            ColorOf("#9CA3AF", SKColors.Gray));

    private ModernWigiDash.Hardware.Service.WigiDashServiceClient? ResolveClient()
        => (Context as IWidgetTelemetryContext)?.TryGetVendorService() as ModernWigiDash.Hardware.Service.WigiDashServiceClient
            ?? VendorService.Instance;

}
