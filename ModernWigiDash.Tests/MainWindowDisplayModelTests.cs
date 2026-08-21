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

    private static readonly PluginInfo FpsPlugin = new("frame_time", "FPS / Frame Time", "System Monitoring", GridSizePreset.Size2x2.ToSize(), typeof(FrameTimeWidget));
    private static readonly PluginInfo WeatherPlugin = new("weather", "Weather Forecast", "Social & Visual", GridSizePreset.Size2x2.ToSize(), typeof(WeatherForecastWidget));
    private static readonly PluginInfo ClockPlugin = new("clock", "Clock", "Utilities", GridSizePreset.Size2x1.ToSize(), typeof(DigitalAnalogClockWidget));

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
