using ModernWigiDash.Hardware.Service;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The dedicated HWiNFO sensor widget (ADR-0022): reads sensor values through
/// the vendor WigiDash service's ISensorValues facet. Distinct from the existing
/// Hardware Monitor widget (which reads LibreHardwareService shared memory);
/// placing one vs. the other IS the source choice. Min/Max are left at 0 (the
/// vendor returns a single reading per sensor). Degrades to the house placeholder
/// when the vendor service is absent (ADR-0017 image).
/// </summary>
[WidgetMetadata("hwinfo", "HWiNFO Sensor", Category = "System Monitoring")]
public sealed class HwinfoWidget : ModernWidgetBase
{
    /// <summary>The "Sensor Index": which sensor from the vendor's list to display.</summary>
    [WidgetProperty("Sensor Index", WidgetPropertyType.Number, "Index into the vendor's sensor list (0-based)", 0f)]
    public float SensorIndex { get; set; } = 0f;

    /// <summary>The "Display Label": overrides the label shown on the widget.</summary>
    [WidgetProperty("Display Label", WidgetPropertyType.Text, "Override the label (leave empty to use the sensor name)", "")]
    public string DisplayLabel { get; set; } = "";

    /// <summary>The "Unit": overrides the unit shown.</summary>
    [WidgetProperty("Unit", WidgetPropertyType.Text, "Override the unit (leave empty to use the sensor's)", "")]
    public string Unit { get; set; } = "";

    /// <summary>The "Decimals": the number of decimal places the hero value shows.</summary>
    [WidgetProperty("Decimals", WidgetPropertyType.Number, "Number of decimal places shown", 1f)]
    public float Decimals { get; set; } = 1f;

    /// <summary>The "Accent Color": the primary accent color.</summary>
    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Primary accent color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    /// <summary>The "Text Color": the header, label, and value color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Header, label, and value color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    private VendorSensorItem? _cachedSensor;
    private double _lastValue;
    private bool _hasReading;

    private readonly SKPaint _headerPaint = new() { IsAntialias = true };
    private readonly SKPaint _valuePaint = new() { IsAntialias = true };
    private readonly SKPaint _unitPaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderTitlePaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderSubPaint = new() { IsAntialias = true };

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var client = VendorService.Instance;
        if (client == null || !client.Sensors.IsReady)
        {
            DrawPlaceholder(canvas, bounds);
            return;
        }

        RefreshReading(client);

        if (!_hasReading)
        {
            DrawPlaceholder(canvas, bounds);
            return;
        }

        var label = string.IsNullOrEmpty(DisplayLabel) ? (_cachedSensor?.Name ?? "Sensor") : DisplayLabel;
        var unit = string.IsNullOrEmpty(Unit) ? (_cachedSensor?.Unit ?? "") : Unit;
        var decimals = Math.Max(0, (int)Decimals);
        var valueStr = _lastValue.ToString($"F{decimals}", CultureInfo.InvariantCulture);

        // Header
        _headerPaint.Color = ColorOf(TextColorHex, SKColors.White);
        var headerFont = FontHelper.GetCachedFont(FontHelper.GeistTypeface, 28f);
        canvas.DrawText(label, bounds.Left + 16f, bounds.Top + 36f, headerFont, _headerPaint);

        // Value
        _valuePaint.Color = ColorOf(AccentColorHex, WidgetPalette.Accent);
        var valueFont = FontHelper.GetCachedFont(FontHelper.GeistTypeface, 48f);
        canvas.DrawText(valueStr, bounds.Left + 16f, bounds.Top + 96f, valueFont, _valuePaint);

        // Unit
        if (!string.IsNullOrEmpty(unit))
        {
            _unitPaint.Color = ColorOf(TextColorHex, SKColors.White);
            var unitFont = FontHelper.GetCachedFont(FontHelper.GeistTypeface, 24f);
            var valueWidth = valueFont.MeasureText(valueStr);
            canvas.DrawText(unit, bounds.Left + 16f + valueWidth + 8f, bounds.Top + 96f, unitFont, _unitPaint);
        }
    }

    private void RefreshReading(WigiDashServiceClient client)
    {
        var index = Math.Max(0, (int)SensorIndex);
        var sensors = client.Sensors.GetSensorList();
        if (sensors == null || index >= sensors.Count)
        {
            _hasReading = false;
            return;
        }

        var sensor = sensors[index];
        _cachedSensor = sensor;
        var reading = client.Sensors.GetSensorValue(sensor.ReadingType, sensor.SensorId1, sensor.SensorId2);
        if (reading == null || !reading.IsValid)
        {
            _hasReading = false;
            return;
        }

        _lastValue = reading.Value;
        _hasReading = true;
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds)
    {
        var text = ColorOf("#9CA3AF", SKColors.Gray);
        TextRenderHelper.DrawTitleSubtitlePlaceholder(
            canvas, bounds,
            "HWiNFO",
            "Vendor service not connected",
            text, _placeholderTitlePaint, _placeholderSubPaint);
    }

    public override ValueTask DisposeAsync()
    {
        _headerPaint.Dispose();
        _valuePaint.Dispose();
        _unitPaint.Dispose();
        _placeholderTitlePaint.Dispose();
        _placeholderSubPaint.Dispose();
        return base.DisposeAsync();
    }
}
