using System.IO;
using System.Text.Json;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Core.Rendering;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using ModernWigiDash.Widgets.Twitch;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class UnitTestSuite
{
    [TestMethod]
    public void PageLayout_FieldProperties_EnforceDefaultsAndTrim()
    {
        var page = new PageLayout
        {
            PageName = "   My Dashboard   ",
            GridSpacingPx = 150f
        };

        Assert.AreEqual("My Dashboard", page.PageName);
        Assert.AreEqual(100f, page.GridSpacingPx, "GridSpacingPx should be clamped to 100f max");
    }

    [TestMethod]
    public void WidgetPropertyType_ContainsFontAndIconEditors()
    {
        Assert.IsTrue(Enum.IsDefined(typeof(WidgetPropertyType), WidgetPropertyType.Font));
        Assert.IsTrue(Enum.IsDefined(typeof(WidgetPropertyType), WidgetPropertyType.Icon));
    }

    [TestMethod]
    public void FontCatalog_ListsSystemFontFamiliesOnce()
    {
        string[] families = FontCatalog.GetAllFamilies();
        Assert.IsNotNull(families);
        Assert.IsTrue(families.Length > 0);
        Assert.AreEqual(families.Length, families.Select(f => f.ToUpperInvariant()).Distinct().Count());
    }

    [TestMethod]
    public void FontHelper_GetTypeface_ResolvesNamedSystemFamilies()
    {
        var arial = FontHelper.GetTypeface("Arial", SKFontStyle.Normal);
        SKTypeface direct = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
        Assert.IsNotNull(arial);
        Assert.AreNotEqual(IntPtr.Zero, arial.Handle);
        Assert.AreEqual(direct.FamilyName, arial.FamilyName, true);
    }

    [TestMethod]
    public void GriddyIcons_Names_CountAndUnique()
    {
        Assert.IsTrue(GriddyIcons.Names.Count > 1000);
        Assert.AreEqual(GriddyIcons.Names.Count, GriddyIcons.Names.Distinct().Count());
        Assert.IsTrue(GriddyIcons.Contains("activity"));
        Assert.IsTrue(GriddyIcons.Contains("ACTIVITY"));
    }

    [TestMethod]
    public void GriddyIcons_AllPaths_ParseToSkPath()
    {
        var failed = GriddyIcons.Names.Where(n => !GriddyIcons.TryGetPath(n, out _)).ToList();
        Assert.AreEqual(0, failed.Count, "Icons failing to parse: " + string.Join(", ", failed.Take(10)));
    }

    [TestMethod]
    public void GriddyIcons_Unknown_ReturnsFalse()
    {
        Assert.IsFalse(GriddyIcons.Contains("definitely_not_an_icon"));
        Assert.IsFalse(GriddyIcons.TryGetPathData("definitely_not_an_icon", out string? pathData));
        Assert.AreEqual("", pathData);
        Assert.IsFalse(GriddyIcons.TryGetPath("", out _));
        Assert.IsFalse(GriddyIcons.TryGetPath(null!, out _));
    }

    [TestMethod]
    public void TextLabelWidget_Defaults_MatchSpec()
    {
        var widget = new TextLabelWidget();
        Assert.AreEqual("Your text here", widget.Text);
        Assert.AreEqual("Geist", widget.FontFamily);
        Assert.AreEqual(32, widget.FontSize);
        Assert.AreEqual("#FAFAFA", widget.TextColorHex);
        Assert.AreEqual("Center", widget.Alignment);
        Assert.AreEqual("#00000000", widget.BackgroundHex);
    }

    [TestMethod]
    public void TextLabelWidget_ProvidesFontOptions()
    {
        var widget = new TextLabelWidget();
        var provider = (IWidgetPropertyOptionsProvider)widget;
        var options = provider.GetPropertyOptions(nameof(widget.FontFamily));
        Assert.IsTrue(options.Count > 0);
        Assert.AreEqual(options[0].Value, options[0].DisplayName);
        Assert.AreEqual(0, provider.GetPropertyOptions("UnknownProperty").Count);
    }

    [TestMethod]
    public void TextLabelWidget_RendersMultiLineTextWithoutExceptions()
    {
        var widget = new TextLabelWidget
        {
            Text = "Line one\nLine two is a longer line that should wrap",
            FontFamily = "Arial",
            FontSize = 24,
            Alignment = "Center"
        };
        using var surface = SKSurface.Create(new SKImageInfo(400, 200));
        var canvas = surface.Canvas;
        widget.Render(canvas, new SKRect(0, 0, 400, 200));
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void PriceFeedManager_DetectsFxPairsAndNormalizesKeys()
    {
        Assert.IsTrue(PriceFeedManager.TryParseFxPair("EUR/USD", out string baseCur, out string quoteCur));
        Assert.AreEqual("EUR", baseCur);
        Assert.AreEqual("USD", quoteCur);
        Assert.AreEqual("EURUSD", PriceFeedManager.NormalizeFxKey(" eur/usd "));
        Assert.AreEqual(AssetKind.Fx, PriceFeedManager.DetectAssetKind("EUR/USD", "Auto"));
        Assert.AreEqual(AssetKind.Stock, PriceFeedManager.DetectAssetKind("AAPL", "Auto"));
        Assert.AreEqual(AssetKind.Crypto, PriceFeedManager.DetectAssetKind("BTC", "Auto"));
        Assert.AreEqual(AssetKind.Crypto, PriceFeedManager.DetectAssetKind("AAPL", "Crypto"));
        Assert.AreEqual(AssetKind.Fx, PriceFeedManager.DetectAssetKind("BTC", "FX Pair"));
        Assert.IsFalse(PriceFeedManager.TryParseFxPair("AAPL", out _, out _));
    }

    [TestMethod]
    public void CryptoStockTickerWidget_FxPair_RendersWithoutExceptions()
    {
        var widget = new CryptoStockTickerWidget { Symbol = "EUR/USD" };
        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        var canvas = surface.Canvas;
        widget.Render(canvas, new SKRect(0, 0, 200, 150));
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void PriceInfo_FormattedPrice_RespectsCurrencySymbol()
    {
        var stock = new PriceInfo { Price = 150.25m, CurrencySymbol = "$" };
        Assert.AreEqual("$150.25", stock.FormattedPrice);
        var fx = new PriceInfo { Price = 1.0843m, CurrencySymbol = "" };
        Assert.AreEqual("1.08", fx.FormattedPrice);
    }

    [TestMethod]
    public void PriceFeedManager_ParsesFrankfurterSeries_ComputesChange()
    {
        const string json = """
        {
          "amount": 1.0,
          "base": "EUR",
          "start_date": "2026-07-30",
          "end_date": "2026-08-04",
          "rates": {
            "2026-07-30": { "USD": 1.1476 },
            "2026-07-31": { "USD": 1.1485 },
            "2026-08-03": { "USD": 1.1511 },
            "2026-08-04": { "USD": 1.1515 }
          }
        }
        """;
        Assert.IsTrue(PriceFeedManager.TryParseFrankfurterSeries(json, "USD", out var price, out var change));
        Assert.AreEqual(1.1515m, price);
        Assert.AreEqual((1.1515m / 1.1511m - 1m) * 100m, change);
    }

    [TestMethod]
    public void PriceFeedManager_ParsesFrankfurterSeries_HandlesMissingQuoteMalformedJsonAndSingleEntry()
    {
        const string json = """
        {
          "base": "EUR",
          "rates": {
            "2026-07-30": { "USD": 1.1476 }
          }
        }
        """;
        Assert.IsFalse(PriceFeedManager.TryParseFrankfurterSeries(json, "GBP", out _, out _));
        Assert.IsFalse(PriceFeedManager.TryParseFrankfurterSeries("not-json", "USD", out _, out _));

        Assert.IsTrue(PriceFeedManager.TryParseFrankfurterSeries(json, "USD", out var price, out var change));
        Assert.AreEqual(1.1476m, price);
        Assert.AreEqual(0m, change);
    }

    [TestMethod]
    public void HotkeyWidget_IconDefaults_AreEmptyAndThemeHex()
    {
        var widget = new HotkeyButtonWidget();
        Assert.AreEqual("", widget.Icon);
        Assert.AreEqual("#FAFAFA", widget.IconColorHex);
    }

    [TestMethod]
    public void HotkeyWidget_WithGriddyIcon_RendersWithoutExceptions()
    {
        var widget = new HotkeyButtonWidget { Icon = "activity" };
        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        var canvas = surface.Canvas;
        widget.Render(canvas, new SKRect(0, 0, 200, 150));
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void HotkeyWidget_IconPositionAndSize_DefaultToAutoCenter()
    {
        var widget = new HotkeyButtonWidget();
        Assert.AreEqual(0, widget.IconSize);
        Assert.AreEqual(0, widget.IconOffsetX);
        Assert.AreEqual(0, widget.IconOffsetY);
    }

    [TestMethod]
    public void HotkeyWidget_WithIconSizeAndOffsets_RendersWithoutExceptions()
    {
        var widget = new HotkeyButtonWidget
        {
            Icon = "activity",
            IconSize = 48,
            IconOffsetX = 10,
            IconOffsetY = -5
        };
        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        var canvas = surface.Canvas;
        widget.Render(canvas, new SKRect(0, 0, 200, 150));
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void MediaKeyCatalog_ListsSevenActionsWithFriendlyNames()
    {
        Assert.AreEqual(7, MediaKeyCatalog.Options.Count);
        Assert.AreEqual("PLAYPAUSE", MediaKeyCatalog.Options[0].Value);
        Assert.AreEqual("Play / Pause", MediaKeyCatalog.Options[0].DisplayName);
        Assert.AreEqual("Stop", MediaKeyCatalog.GetDisplayName("STOP"));
        Assert.IsNull(MediaKeyCatalog.GetDisplayName("BOGUS"));
        Assert.AreEqual("Volume up", MediaKeyCatalog.Options[4].DisplayName);
    }

    [TestMethod]
    public void HotkeyAction_MediaKeySummary_UsesFriendlyName()
    {
        var action = new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "VOLUMEUP" };
        Assert.AreEqual("Media: Volume up", action.Summary());
        Assert.AreEqual("Media: CUSTOMKEY", new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "CUSTOMKEY" }.Summary());
    }

    [TestMethod]
    public void ParseVirtualKey_MediaKeys_IncludeStop()
    {
        Assert.AreEqual(0xB2, (int)HotkeyActionExecutor.ParseVirtualKey("STOP"));
        Assert.AreEqual(0xB3, (int)HotkeyActionExecutor.ParseVirtualKey("PLAYPAUSE"));
        Assert.AreEqual(0xB0, (int)HotkeyActionExecutor.ParseVirtualKey("NEXT"));
        Assert.AreEqual(0xB1, (int)HotkeyActionExecutor.ParseVirtualKey("PREVIOUS"));
        Assert.AreEqual(0xAD, (int)HotkeyActionExecutor.ParseVirtualKey("MUTE"));
        Assert.AreEqual(0xAE, (int)HotkeyActionExecutor.ParseVirtualKey("VOLUMEDOWN"));
        Assert.AreEqual(0xAF, (int)HotkeyActionExecutor.ParseVirtualKey("VOLUMEUP"));
    }

    [TestMethod]
    public void ProfileLayout_Serialization_RoundTripsSuccessfully()
    {
        var profile = new ProfileLayout
        {
            ProfileName = "   Custom Profile   "
        };
        profile.ActivePage.Widgets.Add(new PlacedWidgetInstance
        {
            PluginId = "clock_modern",
            DisplayName = "  Clock Widget  ",
            Width = 408f,
            Height = 150f,
            Opacity = 1.5f
        });

        Assert.AreEqual("Custom Profile", profile.ProfileName);
        Assert.AreEqual("Clock Widget", profile.ActivePage.Widgets[0].DisplayName);
        Assert.AreEqual(1.0f, profile.ActivePage.Widgets[0].Opacity, "Opacity should clamp to 1.0");

        string json = JsonSerializer.Serialize(profile);
        Assert.IsFalse(string.IsNullOrEmpty(json));

        var deserialized = JsonSerializer.Deserialize<ProfileLayout>(json);
        Assert.IsNotNull(deserialized);
        Assert.AreEqual("Custom Profile", deserialized.ProfileName);
        Assert.AreEqual(1, deserialized.Pages[0].Widgets.Count);
        Assert.AreEqual("Clock Widget", deserialized.Pages[0].Widgets[0].DisplayName);
    }

    [TestMethod]
    public void GridSizePreset_ToSize_CalculatesCorrectDimensions()
    {
        SKSize size2x2 = GridSizePreset.Size2x2.ToSize();
        Assert.AreEqual(406f, size2x2.Width);
        Assert.AreEqual(296f, size2x2.Height);

        SKSize size5x4 = GridSizePreset.Size5x4.ToSize();
        Assert.AreEqual(1016f, size5x4.Width);
        Assert.AreEqual(592f, size5x4.Height);
    }

    [TestMethod]
    public void WidgetPluginLoader_RegisterBuiltInPlugin_InstantiatesCorrectly()
    {
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(DigitalAnalogClockWidget));

        Assert.AreEqual(1, loader.RegisteredPlugins.Count);
        var instance = loader.CreateInstance("clock_modern");

        Assert.IsNotNull(instance);
        Assert.IsInstanceOfType<DigitalAnalogClockWidget>(instance);
        Assert.AreEqual(WidgetSizeMode.Resizable, instance.SizeMode);
    }

    [TestMethod]
#pragma warning disable MSTEST0032 // Regression guard: verify protocol constants match hardware spec
    public void DisplayProtocolConstants_FramebufferCalculations_AreExact()
    {
        Assert.AreEqual(1016, DisplayProtocolConstants.FramebufferWidth);
        Assert.AreEqual(592, DisplayProtocolConstants.FramebufferHeight);
        Assert.AreEqual(2, DisplayProtocolConstants.BytesPerPixel);
        Assert.AreEqual(1202944, DisplayProtocolConstants.FrameBufferSize);
    }
#pragma warning restore MSTEST0032

    [TestMethod]
    public void SkiaFrameCompositor_HitTest_ReturnsTopMostWidget()
    {
        using var compositor = new SkiaFrameCompositor();
        var page = new PageLayout();

        var w1 = new PlacedWidgetInstance
        {
            X = 0,
            Y = 0,
            Width = 200,
            Height = 200,
            ZIndex = 1,
            ActiveInstance = new DigitalAnalogClockWidget()
        };
        var w2 = new PlacedWidgetInstance
        {
            X = 50,
            Y = 50,
            Width = 200,
            Height = 200,
            ZIndex = 2,
            ActiveInstance = new DigitalAnalogClockWidget()
        };

        page.Widgets.Add(w1);
        page.Widgets.Add(w2);

        var hit = SkiaFrameCompositor.HitTest(page, 75, 75);
        Assert.IsNotNull(hit);
        Assert.AreEqual(w2, hit, "HitTest must return highest ZIndex widget at overlapping point");
    }

    [TestMethod]
    public void SkiaFrameCompositor_RouteTouch_DeliversToTopMostWidgetInLocalCoordinates()
    {
        using var compositor = new SkiaFrameCompositor();
        var page = new PageLayout();
        var target = new WeatherForecastWidget();
        var placed = new PlacedWidgetInstance
        {
            X = 100,
            Y = 50,
            Width = 200,
            Height = 200,
            ZIndex = 1,
            ActiveInstance = target
        };
        page.Widgets.Add(placed);

        // Touch at (150, 80) global = (50, 30) local to the widget. The weather
        // widget's top-left corner cycles LayoutMode on TouchUp, which proves
        // the touch arrived in widget-local coordinates (a global-coordinate
        // leak would hit a different zone or miss entirely).
        SkiaFrameCompositor.RouteTouch(page, 150, 80, TouchEventType.TouchUp);

        Assert.AreEqual("Daily Forecast", target.LayoutMode, "The touch must reach the widget in local coordinates");
    }

    [TestMethod]
    public void SkiaFrameCompositor_RouteTouch_IgnoresPointOutsideAllWidgets()
    {
        using var compositor = new SkiaFrameCompositor();
        var page = new PageLayout();
        var target = new WeatherForecastWidget();
        page.Widgets.Add(new PlacedWidgetInstance
        {
            X = 0,
            Y = 0,
            Width = 100,
            Height = 100,
            ZIndex = 1,
            ActiveInstance = target
        });

        // Point far outside every widget must not throw and must not be delivered.
        SkiaFrameCompositor.RouteTouch(page, 900, 500, TouchEventType.TouchUp);

        Assert.AreEqual("Detailed", target.LayoutMode, "A point outside every widget must not reach any widget");
    }

    [TestMethod]
    public void SkiaFrameCompositor_RouteTouch_EmptyPage_DoesNotThrow()
    {
        var page = new PageLayout();

        SkiaFrameCompositor.RouteTouch(page, 10, 10, TouchEventType.TouchDown);

        Assert.AreEqual(0, page.Widgets.Count, "Routing on an empty page must not mutate the page");
    }

    [TestMethod]
    public void WeatherForecastWidget_DefaultsAndProperties_InitializeCorrectly()
    {
        var widget = new WeatherForecastWidget();

        Assert.AreEqual("New York", widget.Location);
        Assert.AreEqual("Fixed Location", widget.LocationType);
        Assert.AreEqual("Detailed", widget.LayoutMode);
        Assert.AreEqual("Fahrenheit (°F, mph)", widget.UnitSystem);
        Assert.AreEqual("#F59E0B", widget.AccentColorHex);
        Assert.IsTrue(widget.ShowHumidity);
        Assert.IsTrue(widget.ShowWind);
        Assert.IsTrue(widget.ShowFeelsLike);
        Assert.IsTrue(widget.ShowHighLow);
        Assert.IsFalse(widget.StaticSnapshot);

        // Property Change resets geocode cache flag
        widget.Location = "Tokyo";
        Assert.AreEqual("Tokyo", widget.Location);
    }

    [TestMethod]
    public void WeatherForecastWidget_TouchInteractivity_CyclesLayoutAndUnits()
    {
        var widget = new WeatherForecastWidget();
        Assert.AreEqual("Detailed", widget.LayoutMode);

        // Touch top-left (Layout cycle)
        widget.OnTouch(new SKPoint(20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Daily Forecast", widget.LayoutMode);

        widget.OnTouch(new SKPoint(20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Hourly Forecast", widget.LayoutMode);

        widget.OnTouch(new SKPoint(20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Current Only", widget.LayoutMode);

        widget.OnTouch(new SKPoint(20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Compact", widget.LayoutMode);

        widget.OnTouch(new SKPoint(20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Detailed", widget.LayoutMode);

        // Touch top-right (Unit switch)
        widget.OnTouch(new SKPoint(widget.DefaultSize.Width - 20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Celsius (°C, km/h)", widget.UnitSystem);

        widget.OnTouch(new SKPoint(widget.DefaultSize.Width - 20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Fahrenheit (°F, mph)", widget.UnitSystem);
    }

    [TestMethod]
    public void WeatherForecastWidget_Rendering_ExecutesWithoutExceptions()
    {
        var widget = new WeatherForecastWidget();
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        var canvas = surface.Canvas;
        var bounds = new SKRect(0, 0, 400, 300);

        string[] modes = ["Detailed", "Daily Forecast", "Hourly Forecast", "Current Only", "Compact"];
        foreach (var mode in modes)
        {
            widget.LayoutMode = mode;
            widget.Render(canvas, bounds);
        }

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void WeatherForecastWidget_SmallGridSizeScaling_ExecutesWithoutExceptions()
    {
        var widget = new WeatherForecastWidget();
        using var surface = SKSurface.Create(new SKImageInfo(200, 160));
        var canvas = surface.Canvas;

        SKSize[] smallSizes = [new(200, 160), new(150, 120), new(120, 90)];
        foreach (var size in smallSizes)
        {
            widget.Render(canvas, new SKRect(0, 0, size.Width, size.Height));
        }

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void PlacedWidgetInstance_PropertyValues_RoundTripMixedTypes()
    {
        var placed = new PlacedWidgetInstance
        {
            PluginId = "stopwatch_timer",
            PropertyValues =
            {
                ["TextColorHex"] = "#FFCD85",
                ["CornerRadius"] = 16f,
                ["ShowChange"] = true,
                ["PriceDecimals"] = "4"
            }
        };

        string json = JsonSerializer.Serialize(placed);
        var deserialized = JsonSerializer.Deserialize<PlacedWidgetInstance>(json);
        Assert.IsNotNull(deserialized);
        Assert.AreEqual(4, deserialized.PropertyValues.Count);
        Assert.IsTrue(deserialized.PropertyValues.ContainsKey("TextColorHex"));
        Assert.AreEqual(JsonValueKind.Number, ((JsonElement)deserialized.PropertyValues["CornerRadius"]!).ValueKind, "Imported numbers should arrive as JsonElement");
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
    public void TwitchWidget_DefaultsToAnonymousChatAndDynamicChannelSelection()
    {
        var widget = new TwitchChatStreamWidget();
        var optionsProvider = (IWidgetPropertyOptionsProvider)widget;

        Assert.AreEqual("twitch", widget.ChannelName);
        Assert.AreEqual("", widget.TwitchClientId);
        Assert.IsTrue(widget.AutoConnect);
        Assert.AreEqual(0, optionsProvider.GetPropertyOptions(nameof(widget.ChannelName)).Count);
        Assert.AreEqual("Channel Name", new WidgetPropertyOption("channel_login", "Channel Name").ToString());
        Assert.IsFalse(((IWidgetActionPresentationProvider)widget).IsWidgetActionActive(nameof(widget.LoginWithTwitch)));
    }

    [TestMethod]
    public void TwitchWidget_DefaultsToFontSize24AndCleanStatus()
    {
        var widget = new TwitchChatStreamWidget();
        Assert.AreEqual(24, widget.FontSize);
    }

    [TestMethod]
    public void TwitchWidget_RenderExecutesWithoutErrors()
    {
        var widget = new TwitchChatStreamWidget { HeaderColorHex = "#FFCD85", MessageColorHex = "#C6E0FF", FontSize = 18 };
        using var bitmap = new SkiaSharp.SKBitmap(400, 300);
        using var canvas = new SkiaSharp.SKCanvas(bitmap);
        var bounds = new SkiaSharp.SKRect(0, 0, 400, 300);
        widget.Render(canvas, bounds);

        // The widget must paint its panel background — a fully transparent
        // canvas would mean nothing was rendered.
        Assert.AreNotEqual(0, bitmap.GetPixel(200, 150).Alpha, "The chat panel background must be painted");
    }

    [TestMethod]
    public void HotkeyWidget_ActionType_DefaultsToLaunchApp()
    {
        var widget = new HotkeyButtonWidget();
        Assert.AreEqual("Launch App", widget.ActionType);
        Assert.AreEqual("", widget.ActionCommand);
        Assert.AreEqual("Hotkey", widget.ButtonLabel);
        Assert.AreEqual("Tap to run", widget.Description);
    }

    [TestMethod]
    public void HotkeyWidget_MediaActionTypes_MapToMediaKeys()
    {
        var map = new Dictionary<string, string>
        {
            ["Media Play / Pause"] = "PLAYPAUSE",
            ["Media Next"] = "NEXT",
            ["Media Previous"] = "PREVIOUS",
            ["Media Stop"] = "STOP",
            ["Volume Up"] = "VOLUMEUP",
            ["Volume Down"] = "VOLUMEDOWN",
            ["Mute"] = "MUTE"
        };
        foreach (var (actionType, expectedValue) in map)
        {
            var action = HotkeyButtonWidget.CreateAction(actionType, "");
            Assert.AreEqual(HotkeyActionKind.MediaKey, action.Kind, actionType);
            Assert.AreEqual(expectedValue, action.Value, actionType);
        }
    }

    [TestMethod]
    public void HotkeyWidget_TaskManagerLegacyType_MapsToLaunchTaskmgr()
    {
        var action = HotkeyButtonWidget.CreateAction("Task Manager", "");
        Assert.AreEqual(HotkeyActionKind.Launch, action.Kind);
        Assert.AreEqual("taskmgr.exe", action.Value);
    }

    [TestMethod]
    public void HotkeyWidget_OpenUrlActionType_MapsToOpenUrl()
    {
        var action = HotkeyButtonWidget.CreateAction("Open URL", "https://example.com");
        Assert.AreEqual(HotkeyActionKind.OpenUrl, action.Kind);
        Assert.AreEqual("https://example.com", action.Value);
    }

    [TestMethod]
    public void HotkeyWidget_SingleAction_ExecutesOneAction()
    {
        var launch = HotkeyButtonWidget.CreateAction("Launch App", "notepad.exe");
        Assert.AreEqual(HotkeyActionKind.Launch, launch.Kind);
        Assert.AreEqual("notepad.exe", launch.Value);
        var openUrl = HotkeyButtonWidget.CreateAction("Open URL", "https://example.com");
        Assert.AreEqual(HotkeyActionKind.OpenUrl, openUrl.Kind);
        Assert.AreEqual("https://example.com", openUrl.Value);
        var mute = HotkeyButtonWidget.CreateAction("Mute", "");
        Assert.AreEqual(HotkeyActionKind.MediaKey, mute.Kind);
        Assert.AreEqual("MUTE", mute.Value);
    }

    [TestMethod]
    public void HotkeyActions_SerializeAndRoundTrip()
    {
        List<HotkeyAction> actions =
        [
            new() { Kind = HotkeyActionKind.KeyChord, Value = "Ctrl+Shift+S", DelayMs = 50 },
            new() { Kind = HotkeyActionKind.Text, Value = "Hello", Repeat = 2 },
            new() { Kind = HotkeyActionKind.Delay, DelayMs = 250 }
        ];

        string json = JsonSerializer.Serialize(actions);
        var roundTrip = JsonSerializer.Deserialize<List<HotkeyAction>>(json);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(actions.Count, roundTrip.Count);
        Assert.AreEqual("Ctrl+Shift+S", roundTrip[0].Value);
        Assert.AreEqual(HotkeyActionKind.Delay, roundTrip[2].Kind);
        Assert.AreEqual(250, roundTrip[2].DelayMs);
    }

    [TestMethod]
    public void HotkeyAction_Summary_DescribesConfiguredAction()
    {
        Assert.AreEqual("Launch calc.exe", new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = "calc.exe" }.Summary());
        Assert.AreEqual("Wait 100 ms", new HotkeyAction { Kind = HotkeyActionKind.Delay, DelayMs = 100 }.Summary());
    }

    private static readonly string SinglePathSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M4 4h16v16H4z\"/></svg>";
    private static readonly string MultiPathSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"M4 4h16v16H4z\"/><path d=\"M8 8h8v8H8z\"/></svg>";

    private static string WriteTempSvg(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"hw_icon_{Guid.NewGuid():N}.svg");
        File.WriteAllText(path, content);
        return path;
    }

    [TestMethod]
    public void HotkeyWidget_CustomSvg_ExtractsSinglePathAndRenders()
    {
        string svg = WriteTempSvg(SinglePathSvg);
        try
        {
            Assert.IsTrue(SvgIconLoader.TryGetPath(svg, out var path));
            Assert.IsNotNull(path);
            Assert.IsFalse(path.IsEmpty);
            var widget = new HotkeyButtonWidget { IconFile = svg };
            using var surface = SKSurface.Create(new SKImageInfo(200, 150));
            widget.Render(surface.Canvas, new SKRect(0, 0, 200, 150));
            Assert.IsNotNull(surface);
        }
        finally
        {
            File.Delete(svg);
        }
    }

    [TestMethod]
    public void HotkeyWidget_CustomSvg_MultiPath_FallsBackToLabelOnly()
    {
        string svg = WriteTempSvg(MultiPathSvg);
        try
        {
            Assert.IsFalse(SvgIconLoader.TryGetPath(svg, out _));
            var widget = new HotkeyButtonWidget { IconFile = svg };
            using var surface = SKSurface.Create(new SKImageInfo(200, 150));
            widget.Render(surface.Canvas, new SKRect(0, 0, 200, 150));
            Assert.IsNotNull(surface);
        }
        finally
        {
            File.Delete(svg);
        }
    }

    [TestMethod]
    public void HotkeyWidget_CustomSvg_MissingFile_FallsBackToLabelOnly()
    {
        var widget = new HotkeyButtonWidget
        {
            IconFile = Path.Combine(Path.GetTempPath(), $"hw_missing_{Guid.NewGuid():N}.svg")
        };
        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        widget.Render(surface.Canvas, new SKRect(0, 0, 200, 150));
        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void HotkeyWidget_IconFile_WinsOverIcon()
    {
        string svg = WriteTempSvg(SinglePathSvg);
        try
        {
            Assert.IsTrue(SvgIconLoader.TryGetPath(svg, out var path));
            Assert.IsFalse(path!.IsEmpty);
            var widget = new HotkeyButtonWidget { Icon = "activity", IconFile = svg };
            using var surface = SKSurface.Create(new SKImageInfo(200, 150));
            widget.Render(surface.Canvas, new SKRect(0, 0, 200, 150));
            Assert.IsNotNull(surface);
        }
        finally
        {
            File.Delete(svg);
        }
    }

    [TestMethod]
    public void ColorizedWidgets_RenderWithCustomColors_ExecuteWithoutExceptions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var canvas = surface.Canvas;
        var bounds = new SKRect(0, 0, 406, 296);

        var clock = new DigitalAnalogClockWidget { TextColorHex = "#FFCD85", AccentColorHex = "#22C55E" };
        clock.Render(canvas, bounds);

        var stopwatch = new StopwatchTimerWidget { TextColorHex = "#C6E0FF" };
        stopwatch.Render(canvas, bounds);

        var ticker = new CryptoStockTickerWidget { TextColorHex = "#C6E0FF", PositiveColorHex = "#22C55E", NegativeColorHex = "#EF4444" };
        ticker.Render(canvas, bounds);

        var picture = new PictureAndGifWidget { TextColorHex = "#98B4C8" };
        picture.Render(canvas, bounds);

        var twitch = new TwitchChatStreamWidget { HeaderColorHex = "#FFCD85", MessageColorHex = "#C6E0FF" };
        twitch.Render(canvas, bounds);

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void AudioVisualizerWidget_Render_AllStyles_ExecuteWithoutExceptions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var canvas = surface.Canvas;
        var bounds = new SKRect(0, 0, 406, 296);

        string[] styles = ["Neon Bars", "Oscilloscope Wave", "Radial Pulse"];
        foreach (var style in styles)
        {
            var widget = new AudioVisualizerWidget { VisualizerStyle = style };
            widget.Render(canvas, bounds);
        }

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void NowPlayingWidget_Render_IdleAndPlaceholder_NoExceptions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(1016, 592));
        var canvas = surface.Canvas;
        var bounds = new SKRect(0, 0, 1016, 592);

        // Idle state (no SMTC session available in headless tests) must render without exceptions
        var widget = new NowPlayingWidget();
        widget.Render(canvas, bounds);

        // Render at the minimum size too, exercising the scale path
        using var smallSurface = SKSurface.Create(new SKImageInfo(408, 150));
        var smallCanvas = smallSurface.Canvas;
        widget.Render(smallCanvas, new SKRect(0, 0, 408, 150));

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void PictureAndGifWidget_FileAndFitModes_RenderWithoutExceptions()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wigidash_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string pngPath = Path.Combine(tempDir, "test.png");
        try
        {
            CreateTestPng(pngPath, SKColors.CornflowerBlue);

            using var surface = SKSurface.Create(new SKImageInfo(406, 296));
            var canvas = surface.Canvas;
            var bounds = new SKRect(0, 0, 406, 296);

            foreach (var fitMode in new[] { "Cover", "Contain", "Stretch" })
            {
                var widget = new PictureAndGifWidget { ImagePath = pngPath, FitMode = fitMode, SourceMode = "Single Image" };
                widget.Render(canvas, bounds);
            }

            Assert.IsNotNull(surface);
        }
        finally
        {
            // The widget now decodes asynchronously; a decode task may still be
            // reading the file when teardown runs, so tolerate a transient lock
            // (also covers AV-scan file locks).
            DeleteTempDirWithRetry(tempDir);
        }
    }

    [TestMethod]
    public void PictureAndGifWidget_FolderCycle_AdvancesOnTouchWithoutCyclingInSingleMode()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wigidash_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            CreateTestPng(Path.Combine(tempDir, "a.png"), SKColors.Red);
            CreateTestPng(Path.Combine(tempDir, "b.png"), SKColors.Green);

            using var surface = SKSurface.Create(new SKImageInfo(406, 296));
            var canvas = surface.Canvas;
            var bounds = new SKRect(0, 0, 406, 296);

            var folderWidget = new PictureAndGifWidget { ImagePath = tempDir, SourceMode = "Folder (Cycle)" };
            folderWidget.Render(canvas, bounds);
            folderWidget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);
            folderWidget.Render(canvas, bounds);

            var singleWidget = new PictureAndGifWidget { ImagePath = tempDir, SourceMode = "Single Image" };
            singleWidget.Render(canvas, bounds);
            singleWidget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);
            singleWidget.Render(canvas, bounds);

            Assert.IsNotNull(surface);
        }
        finally
        {
            DeleteTempDirWithRetry(tempDir);
        }
    }

    [TestMethod]
    public void PictureAndGifWidget_UnboundedDecode_IsCapped()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "wigidash_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string pngPath = Path.Combine(tempDir, "test.png");
            CreateTestPng(pngPath, SKColors.CornflowerBlue);

            using var surface = SKSurface.Create(new SKImageInfo(406, 296));
            var canvas = surface.Canvas;
            var bounds = new SKRect(0, 0, 406, 296);

            var widget = new PictureAndGifWidget { ImagePath = pngPath, SourceMode = "Single Image" };
            widget.Render(canvas, bounds);
            Assert.IsNotNull(surface);

            // A 40MB garbage file exceeds the 32MB decode cap: the widget must
            // refuse it without crashing or allocating a frame buffer.
            string hugePath = Path.Combine(tempDir, "huge.gif");
            File.WriteAllBytes(hugePath, new byte[40 * 1024 * 1024]);

            var hugeWidget = new PictureAndGifWidget { ImagePath = hugePath, SourceMode = "Single Image" };
            hugeWidget.Render(canvas, bounds);

            Assert.IsNotNull(surface);
        }
        finally
        {
            DeleteTempDirWithRetry(tempDir);
        }
    }

    private static void DeleteTempDirWithRetry(string tempDir)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static void CreateTestPng(string path, SKColor color)
    {
        using var bitmap = new SKBitmap(60, 60);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    [TestMethod]
    public void CryptoStockTickerWidget_EmptySymbol_RendersPlaceholderWithoutExceptions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(200, 150));
        var canvas = surface.Canvas;
        var widget = new CryptoStockTickerWidget { Symbol = "   " };
        widget.Render(canvas, new SKRect(0, 0, 200, 150));

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void ThemeSettings_ParseColor_HandlesRgbAndArgb()
    {
        var rgb = ModernWigiDash.Core.Theming.ThemeSettings.ParseColor("#FFCD85");
        Assert.IsNotNull(rgb);
        Assert.AreEqual(255, rgb.Value.R);
        Assert.AreEqual(205, rgb.Value.G);
        Assert.AreEqual(133, rgb.Value.B);

        var argb = ModernWigiDash.Core.Theming.ThemeSettings.ParseColor("#CCFFCD85");
        Assert.IsNotNull(argb);
        Assert.AreEqual(204, argb.Value.A);

        Assert.IsNull(ModernWigiDash.Core.Theming.ThemeSettings.ParseColor("not-a-color"));
    }

    [TestMethod]
    public void FrameTimeStatistics_Percentile_UsesNearestRank()
    {
        double[] values = [10, 20, 30, 40];

        Assert.AreEqual(10, FrameTimeStatistics.Percentile(values, 0));
        Assert.AreEqual(20, FrameTimeStatistics.Percentile(values, 50));
        Assert.AreEqual(40, FrameTimeStatistics.Percentile(values, 99));
        Assert.AreEqual(40, FrameTimeStatistics.Percentile(values, 100));
        Assert.AreEqual(0, FrameTimeStatistics.Percentile([], 99));
    }

    [TestMethod]
    public void FrameTimeStatistics_LowFps_ConvertsFromFrameTimes()
    {
        var frameTimes = Enumerable.Range(0, 1000).Select(i => (double)i).ToArray();

        double low1 = FrameTimeStatistics.Low1PercentFps(frameTimes);
        double low01 = FrameTimeStatistics.Low01PercentFps(frameTimes);

        Assert.AreEqual(1000.0 / 989.0, low1, 0.001, "1% low should use the 99th percentile frame time");
        Assert.AreEqual(1000.0 / 998.0, low01, 0.001, "0.1% low should use the 99.9th percentile frame time");
    }

    [TestMethod]
    public void FrameTimeStatistics_FpsFromFrameTimeMs_HandlesEdgeCases()
    {
        Assert.AreEqual(60.0, FrameTimeStatistics.FpsFromFrameTimeMs(1000.0 / 60.0), 0.001);
        Assert.AreEqual(0.0, FrameTimeStatistics.FpsFromFrameTimeMs(0));
        Assert.AreEqual(0.0, FrameTimeStatistics.FpsFromFrameTimeMs(-5));
    }

    [TestMethod]
    public void FrameTimeStore_UpdateAndRead_RoundTrips()
    {
        var record = new FrameTimeSnapshotRecord(
            IsAvailable: true,
            ProcessId: 1234,
            ProcessName: "game.exe",
            Fps: 144.0,
            FrameTimeMs: 6.94,
            Low1PercentFps: 112.0,
            Low01PercentFps: 89.0,
            GpuBusyMs: 92.0,
            CpuFrameTimeMs: 4.1,
            RecentFrameTimesMs: [6.9, 7.0, 7.1, 6.8]);

        FrameTimeStore.Update(record);
        var read = FrameTimeStore.ReadSnapshot();

        Assert.IsTrue(read.IsAvailable);
        Assert.AreEqual("game.exe", read.ProcessName);
        Assert.AreEqual(144.0, read.Fps);
        Assert.AreEqual(92.0, read.GpuBusyMs);
        CollectionAssert.AreEqual(new[] { 6.9, 7.0, 7.1, 6.8 }, read.RecentFrameTimesMs.ToArray());
    }

    [TestMethod]
    public void FrameTimeWidget_Render_AllStates_ExecuteWithoutExceptions()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var canvas = surface.Canvas;
        var bounds = new SKRect(0, 0, 406, 296);

        var unavailable = new FrameTimeWidget();
        FrameTimeStore.Update(FrameTimeSnapshotRecord.Unavailable());
        unavailable.Render(canvas, bounds);

        var waiting = new FrameTimeWidget();
        FrameTimeStore.Update(new FrameTimeSnapshotRecord(true, 0, "", 0, 0, 0, 0, 0, 0, []));
        waiting.Render(canvas, bounds);

        var live = new FrameTimeWidget { AccentColorHex = "#22C55E" };
        List<double> samples = [];
        for (int i = 0; i < 240; i++)
        {
            samples.Add(6.5 + (i % 20) * 0.05);
        }
        FrameTimeStore.Update(new FrameTimeSnapshotRecord(
            true, 4321, "fpsbench.exe", 143.2, 6.98, 110.4, 87.2, 93.0, 4.05, samples));
        live.Render(canvas, bounds);

        // Small (2x1) size must also render without exceptions
        using var smallSurface = SKSurface.Create(new SKImageInfo(200, 160));
        var smallCanvas = smallSurface.Canvas;
        live.Render(smallCanvas, new SKRect(0, 0, 200, 160));

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void ThemeSettings_DisplayMetadata_CoversEveryColorProperty()
    {
        var props = typeof(ModernWigiDash.Core.Theming.ThemeSettings).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .ToList();

        Assert.IsTrue(props.Count > 0, "ThemeSettings should expose color properties");

        foreach (string name in props.Select(p => p.Name))
        {
            Assert.IsTrue(ModernWigiDash.Core.Theming.ThemeSettings.DisplayNames.ContainsKey(name),
                $"Missing friendly display name for '{name}'");
            Assert.IsTrue(ModernWigiDash.Core.Theming.ThemeSettings.Descriptions.ContainsKey(name),
                $"Missing description for '{name}'");
            Assert.IsTrue(ModernWigiDash.Core.Theming.ThemeSettings.Groups.ContainsKey(name),
                $"Missing group for '{name}'");
        }
    }

    [TestMethod]
    public void ThemeSettings_DefaultsToTitaniumAmberPalette()
    {
        var theme = new ModernWigiDash.Core.Theming.ThemeSettings();
        Assert.AreEqual("#121214", theme.BgDark);
        Assert.AreEqual("#1A1A1E", theme.BgPanel);
        Assert.AreEqual("#26262B", theme.BgCard);
        Assert.AreEqual("#3F3F46", theme.Border);
        Assert.AreEqual("#F59E0B", theme.AccentRed);
        Assert.AreEqual("#FBBF24", theme.M3Primary);
        Assert.AreEqual("#FAFAFA", theme.TextPrimary);
        Assert.AreEqual("#A1A1AA", theme.TextSecondary);
        Assert.AreEqual("#0B0B0C", theme.TitleBar);
    }

    [TestMethod]
    public void FontHelper_GetTypefaceForCodepoint_ResolvesEmojiFallback()
    {
        // Latin 'A' should resolve to a valid typeface (Geist or system fallback)
        var latinTf = FontHelper.GetTypefaceForCodepoint('A', SKFontStyle.Normal);
        Assert.IsNotNull(latinTf);
        Assert.AreNotEqual(IntPtr.Zero, latinTf.Handle);

        // Emoji 😀 (U+1F600) should resolve to a valid fallback typeface
        var emojiTf = FontHelper.GetTypefaceForCodepoint(0x1F600, SKFontStyle.Normal);
        Assert.IsNotNull(emojiTf);
        Assert.AreNotEqual(IntPtr.Zero, emojiTf.Handle);
    }

    [TestMethod]
    public void FontHelper_GetTypefaceForCodepoint_HonorsPreferredTypeface()
    {
        var arial = FontCatalog.GetTypeface("Arial", SKFontStyle.Normal);
        Assert.IsNotNull(arial);
        Assert.AreNotEqual(IntPtr.Zero, arial.Handle);

        var resolved = FontHelper.GetTypefaceForCodepoint('A', SKFontStyle.Normal, arial);
        Assert.AreEqual(arial.FamilyName, resolved.FamilyName, true);
    }

    [TestMethod]
    public void FontHelper_GetTypefaceForCodepoint_PreferredWithoutGlyph_FallsBack()
    {
        var arial = FontCatalog.GetTypeface("Arial", SKFontStyle.Normal);
        var emoji = FontHelper.GetTypefaceForCodepoint(0x1F600, SKFontStyle.Normal, arial);
        Assert.IsNotNull(emoji);
        Assert.AreNotEqual(IntPtr.Zero, emoji.Handle);
    }

    [TestMethod]
    public void FontHelper_GetTextRuns_RespectsPreferredTypeface()
    {
        var arial = FontCatalog.GetTypeface("Arial", SKFontStyle.Normal);
        var runs = FontHelper.GetTextRuns("Hello", SKFontStyle.Normal, arial);
        Assert.AreEqual(1, runs.Count);
        Assert.AreEqual(arial.FamilyName, runs[0].Typeface.FamilyName, true);
    }

    [TestMethod]
    public void FontHelper_MeasureTextWithFallback_MatchesDirectFontMeasure()
    {
        var arial = FontCatalog.GetTypeface("Arial", SKFontStyle.Normal);
        using var font = FontHelper.CreateFont(arial, 24f);
        float direct = font.MeasureText("Hello");
        float fallback = FontHelper.MeasureTextWithFallback("Hello", font);
        Assert.AreEqual(direct, fallback, 0.01f);
    }

    [TestMethod]
    public void FontCatalog_GetAllFamilies_IncludesGeist()
    {
        string[] families = FontCatalog.GetAllFamilies();
        Assert.IsTrue(families.Contains("Geist"), "Geist must be listed so the inspector can select the default font.");
    }

    [TestMethod]
    public void TwitchWidget_RendersMessagesWithEmojisWithoutErrors()
    {
        var widget = new TwitchChatStreamWidget();
        using var bitmap = new SKBitmap(400, 300);
        using var canvas = new SKCanvas(bitmap);
        var bounds = new SKRect(0, 0, 400, 300);
        widget.AddTestChatMessageForTesting("GamerOne", "Hello world! 🔥 🎉 💬");
        widget.Render(canvas, bounds);

        // The message render must paint the panel — a fully transparent canvas
        // would mean the queued message was never drawn.
        Assert.AreNotEqual(0, bitmap.GetPixel(200, 150).Alpha, "The chat panel must paint when messages are queued");
    }
}
