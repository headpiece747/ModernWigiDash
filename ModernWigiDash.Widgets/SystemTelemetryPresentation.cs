using System.Globalization;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

/// <summary>
/// One hardware-monitor display state: either an unavailable placeholder or
/// a reading display (resolved label/unit, the formatted hero value, the
/// mode, and the gauge/bar progress fraction).
/// </summary>
public sealed record SystemTelemetryDisplay(
    bool HasReading,
    SystemTelemetryDisplayMode Mode,
    string PlaceholderTitle,
    string PlaceholderSubtitle,
    string Label,
    string Unit,
    string ValueText,
    float Progress);

/// <summary>
/// Pure presentation rules for the Hardware Monitor widget: the three
/// unavailable placeholders, the label/unit override resolution, the
/// unknown-mode → Gauge fallback, the clamped invariant-culture hero value,
/// the gauge/bar maximum resolution with its value-derived floor, and the
/// 0..1 progress clamp. The widget's render methods are thin adapters that
/// lay these out — the display rules are assertable without pixels.
/// </summary>
public static class SystemTelemetryPresentation
{
    public const string NoDataTitle = "No sensor data";
    public const string NoDataSubtitle = "Start LibreHardwareService to read hardware sensors";
    public const string NoSensorTitle = "Select a sensor";
    public const string NoSensorSubtitle = "Open Settings and pick a sensor reading";
    public const string SensorMissingTitle = "Sensor not found";

    /// <summary>The store gate: no fresh connected snapshot at all.</summary>
    public static SystemTelemetryDisplay NoSensorData()
        => new(false, SystemTelemetryDisplayMode.Gauge, NoDataTitle, NoDataSubtitle, string.Empty, string.Empty, string.Empty, 0f);

    /// <summary>A live snapshot, but no sensor is selected yet.</summary>
    public static SystemTelemetryDisplay NoSensorSelected()
        => new(false, SystemTelemetryDisplayMode.Gauge, NoSensorTitle, NoSensorSubtitle, string.Empty, string.Empty, string.Empty, 0f);

    /// <summary>A live snapshot, but the selected sensor is absent from it.</summary>
    public static SystemTelemetryDisplay SensorNotPresent(string label)
        => new(false, SystemTelemetryDisplayMode.Gauge, SensorMissingTitle, $"{label} is not currently available", string.Empty, string.Empty, string.Empty, 0f);

    /// <summary>
    /// The reading display: the label/unit overrides fall back to the reading's
    /// own, the display mode parses with the shared rule (unknown → Gauge),
    /// the hero value formats invariant with the decimal count clamped, and
    /// the progress derives from the resolved maximum.
    /// </summary>
    public static SystemTelemetryDisplay Build(
        SensorReadingDto reading,
        float value,
        string displayLabelOverride,
        string unitOverride,
        string displayMode,
        bool autoScale,
        float maxValue,
        float decimals)
    {
        string label = string.IsNullOrWhiteSpace(displayLabelOverride) ? reading.Label : displayLabelOverride;
        string unit = string.IsNullOrWhiteSpace(unitOverride) ? reading.Unit : unitOverride;
        float max = ResolveMax(autoScale, reading.Max, maxValue, value);

        return new SystemTelemetryDisplay(
            HasReading: true,
            Mode: SystemTelemetryDisplayModeParser.Parse(displayMode),
            PlaceholderTitle: string.Empty,
            PlaceholderSubtitle: string.Empty,
            Label: label,
            Unit: unit,
            ValueText: FormatValue(value, decimals),
            Progress: GaugeFraction(value, max));
    }

    /// <summary>
    /// The hero value in invariant culture with the decimal count clamped to
    /// 0..3 (the format strings are precomputed — one array lookup, no string
    /// building per frame).
    /// </summary>
    public static string FormatValue(float value, float decimals)
        => value.ToString(ValueFormats[Math.Clamp((int)MathF.Round(decimals), 0, 3)], CultureInfo.InvariantCulture);

    private static readonly string[] ValueFormats = ["F0", "F1", "F2", "F3"];

    /// <summary>
    /// The gauge/bar maximum: the sensor's recorded peak when auto scale is on
    /// (floored by the current value), else the manual max. A value-derived
    /// floor keeps a zero/negative max from producing a division-by-zero gauge.
    /// </summary>
    public static float ResolveMax(bool autoScale, double sensorMax, float maxValue, float value)
    {
        double reference = autoScale ? Math.Max(sensorMax, value) : maxValue;
        return reference > 0 ? (float)reference : Math.Max(1f, value * 1.2f);
    }

    /// <summary>
    /// The value progress fraction clamped into 0..1 (shared by the gauge and
    /// bar tracks). A non-positive max can never divide by zero.
    /// </summary>
    public static float GaugeFraction(float value, float max)
        => Math.Clamp(value / Math.Max(1f, max), 0f, 1f);
}
