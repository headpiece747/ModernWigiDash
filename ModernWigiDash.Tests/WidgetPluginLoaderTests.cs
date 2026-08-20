using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using ModernWigiDash.Widgets.Twitch;
using SkiaSharp;

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
        Assert.AreEqual(406f, instance.DefaultSize.Width, "the clock's 2x1 default size must come from its [WidgetMetadata] DefaultGridSize preset");
    }

    [TestMethod]
    public void WidgetPluginLoader_CatalogDefaultSize_AgreesWithInstances()
    {
        // Placement reads the catalog entry's nominal size (resolved from the
        // [WidgetMetadata] preset at registration) while PlaceWidget sizes
        // from the instance's DefaultSize (derived from the same attribute):
        // for widgets that only declare the preset, the two facts agree.
        // Pin the agreement so a future attribute/instance drift cannot make
        // centered placement disagree with the placed size.
        var loader = new WidgetPluginLoader();
        Type[] declaredWidgets =
        [
            typeof(DigitalAnalogClockWidget), // 2x1
            typeof(TwitchChatStreamWidget),   // 2x4
            typeof(AudioVisualizerWidget),    // 5x2
            typeof(WeatherForecastWidget),    // 5x4
            typeof(NowPlayingWidget),         // 5x4
            typeof(FrameTimeWidget)           // 2x2 (attribute default)
        ];
        foreach (var widgetType in declaredWidgets)
        {
            loader.RegisterBuiltInPlugin(widgetType);
        }

        foreach (var info in loader.RegisteredPlugins)
        {
            var widget = loader.CreateInstance(info.PluginId);
            Assert.IsNotNull(widget, $"{info.PluginId} must instantiate for the agreement check");
            Assert.AreEqual(info.DefaultSize.Width, widget.DefaultSize.Width,
                $"{info.PluginId}: the catalog's nominal size must equal the instance's DefaultSize");
            Assert.AreEqual(info.DefaultSize.Height, widget.DefaultSize.Height,
                $"{info.PluginId}: the catalog's nominal size must equal the instance's DefaultSize");
        }
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

    [TestMethod]
    public void WidgetPluginLoader_CreateInstanceResult_UnknownIdReturnsNotFound()
    {
        var loader = new WidgetPluginLoader();

        var result = loader.CreateInstanceResult("no_such_plugin");

        Assert.IsInstanceOfType<WidgetCreateResult.NotFound>(result);
    }

    [TestMethod]
    public void WidgetPluginLoader_CreateInstanceResult_ThrowingConstructorReturnsBrokenWithReason()
    {
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(ThrowingCtorWidget));

        var result = loader.CreateInstanceResult(nameof(ThrowingCtorWidget));

        Assert.IsInstanceOfType<WidgetCreateResult.Broken>(result);
        StringAssert.Contains(((WidgetCreateResult.Broken)result).Reason, "boom");
    }

    [TestMethod]
    public void WidgetPluginLoader_CreateInstanceResult_RegisteredWidgetReturnsOk()
    {
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(DigitalAnalogClockWidget));

        var result = loader.CreateInstanceResult("clock_modern");

        Assert.IsInstanceOfType<WidgetCreateResult.Ok>(result);
        Assert.IsInstanceOfType<DigitalAnalogClockWidget>(((WidgetCreateResult.Ok)result).Widget);
    }

    /// <summary>A widget whose constructor throws — the Broken-result pin for
    /// <see cref="WidgetPluginLoader.CreateInstanceResult"/>. Internal (not
    /// private) because the constructor is only reached through reflection;
    /// the unused-member analyzer would flag a private one.</summary>
    internal sealed class ThrowingCtorWidget : ModernWidgetBase
    {
        public ThrowingCtorWidget() => throw new InvalidOperationException("boom");

        public override void Render(SKCanvas canvas, SKRect bounds)
        {
        }
    }
}
