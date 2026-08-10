using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class SystemTelemetryDisplayModeTests
{
    [TestMethod]
    public void Parse_KnownModes_MapExactly()
    {
        Assert.AreEqual(SystemTelemetryDisplayMode.Gauge, SystemTelemetryDisplayModeParser.Parse("Gauge"));
        Assert.AreEqual(SystemTelemetryDisplayMode.Bar, SystemTelemetryDisplayModeParser.Parse("Bar"));
        Assert.AreEqual(SystemTelemetryDisplayMode.Value, SystemTelemetryDisplayModeParser.Parse("Value"));
        Assert.AreEqual(SystemTelemetryDisplayMode.Graph, SystemTelemetryDisplayModeParser.Parse("Graph"));
    }

    [TestMethod]
    public void Parse_UnknownMode_DefaultsToGauge()
    {
        Assert.AreEqual(SystemTelemetryDisplayMode.Gauge, SystemTelemetryDisplayModeParser.Parse("Bogus"));
        Assert.AreEqual(SystemTelemetryDisplayMode.Gauge, SystemTelemetryDisplayModeParser.Parse(null));
    }
}
