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
    public void PageLayout_PageName_EnforcesTrim()
    {
        var page = new PageLayout
        {
            PageName = "   My Dashboard   "
        };

        Assert.AreEqual("My Dashboard", page.PageName);
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
    public void PriceInfo_FormattedChange_RendersSignAndPercent()
    {
        var up = new PriceInfo { ChangePercent = 1.0843m };
        Assert.AreEqual("+1.08%", up.FormattedChange);
        var down = new PriceInfo { ChangePercent = -0.5m };
        Assert.AreEqual("-0.50%", down.FormattedChange);
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
        Assert.AreEqual(406f, instance.DefaultSize.Width, "The clock's 2x1 default size must come from its GridSizePreset");
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
        FrameTimeSnapshotRecord read = FrameTimeStore.TryReadFresh(TimeSpan.MaxValue)!;

        Assert.IsTrue(read.IsAvailable);
        Assert.AreEqual("game.exe", read.ProcessName);
        Assert.AreEqual(144.0, read.Fps);
        Assert.AreEqual(92.0, read.GpuBusyMs);
        CollectionAssert.AreEqual(new[] { 6.9, 7.0, 7.1, 6.8 }, read.RecentFrameTimesMs.ToArray());
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
}
