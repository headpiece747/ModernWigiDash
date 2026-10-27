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
    [WidgetProperty("Sensor", WidgetPropertyType.Choice, "HWiNFO sensor exposed by the vendor service", "")]
    public string SensorKey { get; set; } = "";

    // The refresh core (the ADR-0022 image): the cadence, the key codec, the
    // read throttle, and the empty-pick fallback in one module, driven with
    // one call per render tick through the client's ISensorValues facet.
    private readonly VendorSensorRefresh _refresh = new();

    /// <inheritdoc />
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var (accent, text) = ResolveColors();

        var client = (Context as IWidgetTelemetryContext)?.TryGetVendorService() as ModernWigiDash.Hardware.Service.WigiDashServiceClient
            ?? VendorService.Instance;
        if (client == null)
        {
            DrawPlaceholder(canvas, bounds, serviceReady: false);
            return;
        }

        // No readiness gate here: the refresh core's list refresh (every 5 s)
        // drives the client's self-heal, so a vendor service that starts or
        // restarts after the app is picked up without a relaunch.
        var reading = _refresh.Refresh(client.Sensors, SensorKey);
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

        var client = (Context as IWidgetTelemetryContext)?.TryGetVendorService() as ModernWigiDash.Hardware.Service.WigiDashServiceClient
            ?? VendorService.Instance;
        var sensors = client?.Sensors.GetSensorList();
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

                return new WidgetPropertyOption(VendorSensorRefresh.ComposeKey(s.ReadingType, s.SensorId1, s.SensorId2), label);
            })
            .ToArray();
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds, bool serviceReady)
        => Renderer.DrawPlaceholder(
            canvas,
            bounds,
            "HWiNFO",
            serviceReady ? "No sensor data" : "Vendor service not connected",
            ColorOf("#9CA3AF", SKColors.Gray));

}
