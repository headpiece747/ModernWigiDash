using ModernWigiDash.App;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's last untested stateful logic: the USB badge mapping and the
/// catalog filter/sort — pure now, assertable without WPF.
/// </summary>
[TestClass]
public class MainWindowDisplayModelTests
{
    [TestMethod]
    public void UsbBadgeModel_EveryState_MapsToLabelAndBrush()
    {
        Assert.AreEqual(("Connected", "AccentGreen"), UsbBadgeModel.From(ConnectionState.Connected));
        Assert.AreEqual(("Simulated", "AccentRed"), UsbBadgeModel.From(ConnectionState.Simulated));
        Assert.AreEqual(("Connecting", "DangerBorder"), UsbBadgeModel.From(ConnectionState.Connecting));
        Assert.AreEqual(("Disconnected", "DangerBorder"), UsbBadgeModel.From(ConnectionState.Disconnected));
    }

    private static readonly PluginInfo FpsPlugin = new() { PluginId = "frame_time", DisplayName = "FPS / Frame Time", Category = "System Monitoring" };
    private static readonly PluginInfo WeatherPlugin = new() { PluginId = "weather", DisplayName = "Weather Forecast", Category = "Social & Visual" };
    private static readonly PluginInfo ClockPlugin = new() { PluginId = "clock", DisplayName = "Clock", Category = "Utilities" };

    [TestMethod]
    public void CatalogFilter_EmptyQuery_SortsAllByName()
    {
        var result = CatalogFilter.Apply([WeatherPlugin, ClockPlugin, FpsPlugin], "");

        CollectionAssert.AreEqual(new[] { "Clock", "FPS / Frame Time", "Weather Forecast" }, result.Select(p => p.DisplayName).ToArray());
    }

    [TestMethod]
    public void CatalogFilter_MatchesNameAndCategoryCaseInsensitively()
    {
        var byName = CatalogFilter.Apply([WeatherPlugin, ClockPlugin, FpsPlugin], "fps");
        CollectionAssert.AreEqual(new[] { "FPS / Frame Time" }, byName.Select(p => p.DisplayName).ToArray());

        var byCategory = CatalogFilter.Apply([WeatherPlugin, ClockPlugin, FpsPlugin], "visual");
        CollectionAssert.AreEqual(new[] { "Weather Forecast" }, byCategory.Select(p => p.DisplayName).ToArray());
    }

    [TestMethod]
    public void CatalogFilter_NoMatch_Empty()
    {
        var result = CatalogFilter.Apply([WeatherPlugin, ClockPlugin, FpsPlugin], "zzz");
        Assert.AreEqual(0, result.Count);
    }
}
