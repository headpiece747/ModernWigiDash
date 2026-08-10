using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Widgets;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

[TestClass]
public class WidgetPluginLoaderTests
{
    [TestMethod]
    public void WidgetPluginLoader_RegisterBuiltInPlugin_InstantiatesCorrectly()
    {
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(DigitalAnalogClockWidget));

        Assert.AreEqual(1, loader.RegisteredPlugins.Count);
        var instance = loader.CreateInstance("clock_modern");

        Assert.IsNotNull(instance);
        Assert.IsInstanceOfType<DigitalAnalogClockWidget>(instance);
        Assert.AreEqual(406f, instance.DefaultSize.Width, "The clock's 2x1 default size must come from its GridSizePreset");
    }

    [TestMethod]
    public void WidgetColorProperties_Defaults_UseThemeHexes()
    {
        Assert.AreEqual("#FAFAFA", new DigitalAnalogClockWidget().TextColorHex);
        Assert.AreEqual("#F59E0B", new HardwareMonitorWidget().AccentColorHex);
        Assert.AreEqual("#F59E0B", new FrameTimeWidget().AccentColorHex);
        Assert.AreEqual("#FAFAFA", new FrameTimeWidget().TextColorHex);
        Assert.AreEqual("#F59E0B", new NowPlayingWidget().AccentColorHex);
        Assert.AreEqual("#FAFAFA", new HotkeyButtonWidget().TextColorHex);
        Assert.AreEqual("#FAFAFA", new StopwatchTimerWidget().TextColorHex);
        Assert.AreEqual("#FAFAFA", new CryptoStockTickerWidget().TextColorHex);
        Assert.AreEqual("#FAFAFA", new PictureAndGifWidget().TextColorHex);
        Assert.AreEqual("#F59E0B", new HotkeyButtonWidget().ButtonColorHex);
        Assert.AreEqual("#F8FAFC", new TwitchChatStreamWidget().MessageColorHex);
        Assert.AreEqual("#FAFAFA", new TextLabelWidget().TextColorHex);
    }

    // ── loader branch coverage (moved from the residual-coverage grab-bag) ──

    [TestMethod]
    public void WidgetPluginLoader_NonWidgetTypes_Skipped()
    {
        var loader = new WidgetPluginLoader();

        loader.RegisterBuiltInPlugin(typeof(string));

        Assert.AreEqual(0, loader.RegisteredPlugins.Count, "a non-widget type must not register");
    }

    [TestMethod]
    public void WidgetPluginLoader_CreateInstance_UnknownIdReturnsNull()
    {
        var loader = new WidgetPluginLoader();

        Assert.IsNull(loader.CreateInstance("no_such_plugin"));
    }
}
