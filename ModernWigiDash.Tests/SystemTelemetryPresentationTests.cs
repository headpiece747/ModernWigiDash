using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The <see cref="SystemTelemetryPresentation"/> display rules — pure, no
/// pixels: the unavailable placeholders, the label/unit override resolution,
/// the unknown-mode fallback, the clamped invariant value format, the
/// maximum resolution with its value-derived floor, and the progress clamp.
/// </summary>
[TestClass]
public class SystemTelemetryPresentationTests
{
    private static readonly SensorReadingDto CpuTemp = new()
    {
        SensorId = "cpu-temp",
        SensorName = "CPU Package",
        HardwareName = "Mainboard",
        Unit = "°C",
        Value = 55.5,
        Min = 40,
        Max = 90,
    };

    // ── Unavailable placeholders ─────────────────────────────────

    [TestMethod]
    public void NoSensorData_ReadsStoreGatePlaceholder()
    {
        var display = SystemTelemetryPresentation.NoSensorData();

        Assert.IsFalse(display.HasReading);
        Assert.AreEqual("No sensor data", display.PlaceholderTitle);
        Assert.AreEqual("Start LibreHardwareService to read hardware sensors", display.PlaceholderSubtitle);
        Assert.AreEqual(SystemTelemetryDisplayMode.Gauge, display.Mode, "a placeholder has no reading to mode-display");
    }

    [TestMethod]
    public void NoSensorSelected_ReadsSelectionHint()
    {
        var display = SystemTelemetryPresentation.NoSensorSelected();

        Assert.IsFalse(display.HasReading);
        Assert.AreEqual("Select a sensor", display.PlaceholderTitle);
        Assert.AreEqual("Open Settings and pick a sensor reading", display.PlaceholderSubtitle);
    }

    [TestMethod]
    public void SensorNotPresent_NamesTheMissingSensor()
    {
        var display = SystemTelemetryPresentation.SensorNotPresent("Mainboard: GPU Temp");

        Assert.IsFalse(display.HasReading);
        Assert.AreEqual("Sensor not found", display.PlaceholderTitle);
        Assert.AreEqual("Mainboard: GPU Temp is not currently available", display.PlaceholderSubtitle);
    }

    // ── Build: override + mode + format rules ────────────────────

    [TestMethod]
    public void Build_NoOverrides_UsesReadingIdentity()
    {
        var display = SystemTelemetryPresentation.Build(CpuTemp, 55.5f, "", "", "Gauge", true, 100f, 1f);

        Assert.IsTrue(display.HasReading);
        Assert.AreEqual("Mainboard: CPU Package", display.Label, "an empty DisplayLabel falls back to the reading's own label");
        Assert.AreEqual("°C", display.Unit, "an empty Unit falls back to the reading's unit");
        Assert.AreEqual(SystemTelemetryDisplayMode.Gauge, display.Mode);
        Assert.AreEqual(string.Empty, display.PlaceholderTitle, "a reading display has no placeholder");
    }

    [TestMethod]
    public void Build_Overrides_WinOverReadingIdentity()
    {
        var display = SystemTelemetryPresentation.Build(CpuTemp, 55.5f, "My Label", "X", "Bar", true, 100f, 0f);

        Assert.AreEqual("My Label", display.Label);
        Assert.AreEqual("X", display.Unit);
        Assert.AreEqual(SystemTelemetryDisplayMode.Bar, display.Mode);
    }

    [TestMethod]
    public void Build_WhitespaceOverrides_FallBackToReadingIdentity()
    {
        var display = SystemTelemetryPresentation.Build(CpuTemp, 55.5f, "   ", "  ", "Gauge", true, 100f, 0f);

        Assert.AreEqual("Mainboard: CPU Package", display.Label, "whitespace counts as no override");
        Assert.AreEqual("°C", display.Unit, "whitespace counts as no override");
    }

    [TestMethod]
    public void Build_UnknownMode_FallsBackToGauge()
    {
        var display = SystemTelemetryPresentation.Build(CpuTemp, 55.5f, "", "", "Not A Mode", true, 100f, 0f);

        Assert.AreEqual(SystemTelemetryDisplayMode.Gauge, display.Mode, "a hand-edited profile value parses to the property default");
    }

    [TestMethod]
    public void Build_KnownModes_ParseForEach()
    {
        Assert.AreEqual(SystemTelemetryDisplayMode.Bar, SystemTelemetryPresentation.Build(CpuTemp, 55.5f, "", "", "Bar", true, 100f, 0f).Mode);
        Assert.AreEqual(SystemTelemetryDisplayMode.Value, SystemTelemetryPresentation.Build(CpuTemp, 55.5f, "", "", "Value", true, 100f, 0f).Mode);
        Assert.AreEqual(SystemTelemetryDisplayMode.Graph, SystemTelemetryPresentation.Build(CpuTemp, 55.5f, "", "", "Graph", true, 100f, 0f).Mode);
    }

    [TestMethod]
    public void FormatValue_FixedDecimals_InvariantCulture()
    {
        Assert.AreEqual("55.5", SystemTelemetryPresentation.FormatValue(55.5f, 1f));
        Assert.AreEqual("56", SystemTelemetryPresentation.FormatValue(55.5f, 0f));
        Assert.AreEqual("55.50", SystemTelemetryPresentation.FormatValue(55.5f, 2f));
    }

    [TestMethod]
    public void FormatValue_DecimalCount_ClampedIntoRange()
    {
        Assert.AreEqual("56", SystemTelemetryPresentation.FormatValue(55.5f, -2f), "below 0 clamps to F0");
        Assert.AreEqual("55.500", SystemTelemetryPresentation.FormatValue(55.5f, 9f), "above 3 clamps to F3");
    }

    // ── Maximum resolution ───────────────────────────────────────

    [TestMethod]
    public void ResolveMax_AutoScale_UsesSensorPeak_OrValue()
    {
        Assert.AreEqual(90f, SystemTelemetryPresentation.ResolveMax(true, CpuTemp.Max, 100f, 55.5f), "Auto scale must use the sensor's recorded max");
        Assert.AreEqual(95f, SystemTelemetryPresentation.ResolveMax(true, CpuTemp.Max, 100f, 95f), "A value above the sensor max must win");
    }

    [TestMethod]
    public void ResolveMax_AutoScale_ZeroPeak_FallsBackToValueDerivedFloor()
    {
        float max = SystemTelemetryPresentation.ResolveMax(true, 0.0, 100f, 5f);

        Assert.IsTrue(max > 0, "A zero/negative peak must fall back to a value-derived floor");
    }

    [TestMethod]
    public void ResolveMax_Manual_UsesMaxValue()
    {
        Assert.AreEqual(120f, SystemTelemetryPresentation.ResolveMax(false, CpuTemp.Max, 120f, 55.5f), "Manual mode must use MaxValue");
    }

    [TestMethod]
    public void ResolveMax_Manual_InvalidMax_FallsBackToValueDerivedFloor()
    {
        float max = SystemTelemetryPresentation.ResolveMax(false, CpuTemp.Max, 0f, 55.5f);

        Assert.IsTrue(max > 0, "A non-positive MaxValue must fall back to a value-derived floor");
    }

    // ── Progress + Build integration ─────────────────────────────

    [TestMethod]
    public void GaugeFraction_ClampsIntoUnitRange()
    {
        Assert.AreEqual(0.5f, SystemTelemetryPresentation.GaugeFraction(50f, 100f), 1e-6f);
        Assert.AreEqual(0f, SystemTelemetryPresentation.GaugeFraction(-5f, 100f), "negative values clamp to zero");
        Assert.AreEqual(1f, SystemTelemetryPresentation.GaugeFraction(150f, 100f), "over-max values clamp to one");
        Assert.AreEqual(1f, SystemTelemetryPresentation.GaugeFraction(50f, 0f), "a non-positive max must not divide by zero");
    }

    [TestMethod]
    public void Build_Progress_DerivesFromResolvedMaximum()
    {
        var display = SystemTelemetryPresentation.Build(CpuTemp, 45f, "", "", "Gauge", true, 100f, 0f);

        Assert.AreEqual(0.5f, display.Progress, 1e-6f, "value 45 against the sensor peak of 90 is half");
    }

    [TestMethod]
    public void Build_Progress_ClampsOverAndUnderRange()
    {
        Assert.AreEqual(1f, SystemTelemetryPresentation.Build(CpuTemp, 999f, "", "", "Gauge", true, 100f, 0f).Progress);
        Assert.AreEqual(0f, SystemTelemetryPresentation.Build(CpuTemp, -5f, "", "", "Gauge", true, 100f, 0f).Progress);
    }
}
