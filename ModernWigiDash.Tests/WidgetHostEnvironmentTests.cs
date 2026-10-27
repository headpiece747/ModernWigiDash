using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModernWigiDash.App;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class WidgetHostEnvironmentTests
{
    private sealed class TestWidget : ModernWidgetBase, IGlobalHotkeyProvider
    {
        public override void Render(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect bounds) { }
        public bool AffectsGlobalHotkey(string propertyName) => propertyName == "TriggerKey";
        public bool TryGetGlobalHotkey(out int modFlags, out ushort virtualKey, out string chord)
        {
            modFlags = 0;
            virtualKey = 0;
            chord = string.Empty;
            return false;
        }
        public void FireGlobalHotkey() { }
    }

    [TestMethod]
    public void Construction_Defaults_DoNotThrowOnAnyInvocation()
    {
        var env = new WidgetHostEnvironment();

        // Safe no-ops
        env.RequestRender();
        env.RequestInspectorRefresh();
        env.NavigatePage(1);
        env.NavigatePage(0);
        env.LaunchAutoHotkeyScript(@"C:\fake\script.ahk");
        env.CloseDeviceAuthorization();
        env.ShowDeviceAuthorization("Service", new Uri("https://auth.example.com"), "CODE", DateTimeOffset.UtcNow);

        Assert.IsNull(env.TryReadSensors());
        Assert.IsNull(env.TryReadFrameTime());
    }

    [TestMethod]
    public void CustomSinks_ReceiveLogAndTelemetryCalls()
    {
        string? infoMessage = null;
        string? errMessage = null;
        Exception? capturedEx = null;

        var sensorDto = new SensorSnapshotDto { IsConnected = true };
        var frameDto = new FrameTimeSnapshotDto { IsAvailable = true };

        var env = new WidgetHostEnvironment(
            logInfoSink: m => infoMessage = m,
            logErrorSink: (m, ex) => { errMessage = m; capturedEx = ex; },
            sensorProvider: () => sensorDto,
            frameTimeProvider: () => frameDto);

        env.LogInfo("Test info");
        Assert.AreEqual("Test info", infoMessage);

        var testEx = new InvalidOperationException("boom");
        env.LogError("Test error", testEx);
        Assert.AreEqual("Test error", errMessage);
        Assert.AreSame(testEx, capturedEx);

        Assert.AreSame(sensorDto, env.TryReadSensors());
        Assert.AreSame(frameDto, env.TryReadFrameTime());
    }

    [TestMethod]
    public void PersistProperty_UpdatesPlacedWidget_AndTriggersGlobalHotkeyRefresh()
    {
        var widgetInstance = new TestWidget();
        var placed = new PlacedWidgetInstance
        {
            InstanceId = Guid.NewGuid().ToString(),
            PluginId = "TestPlugin",
            ActiveInstance = widgetInstance
        };
        var profile = new ProfileLayout
        {
            Pages = [new PageLayout { Widgets = [placed] }]
        };

        bool dirtyMarked = false;
        bool hotkeysRefreshed = false;

        var env = new WidgetHostEnvironment(
            profileProvider: () => profile,
            markProfileDirty: () => dirtyMarked = true,
            refreshGlobalHotkeys: () => hotkeysRefreshed = true);

        env.PersistProperty(widgetInstance, "TriggerKey", "Ctrl+Alt+T");

        Assert.IsTrue(dirtyMarked);
        Assert.IsTrue(hotkeysRefreshed);
        Assert.AreEqual("Ctrl+Alt+T", placed.PropertyValues["TriggerKey"]);
    }

    [TestMethod]
    public void SaveCalendarCredential_ValidCredentials_DispatchesOnUiThread()
    {
        string? savedFeed = null;
        string? savedPass = null;
        int uiRuns = 0;

        var env = new WidgetHostEnvironment(
            onUiThread: a => { uiRuns++; a(); },
            saveCalendarCredential: (feed, pass) => { savedFeed = feed; savedPass = pass; });

        env.SaveCalendarCredential("cal1", "secret123");

        Assert.AreEqual(1, uiRuns);
        Assert.AreEqual("cal1", savedFeed);
        Assert.AreEqual("secret123", savedPass);
    }

    [TestMethod]
    public void SaveCalendarCredential_BlankCredentials_LogsErrorAndDoesNotDispatch()
    {
        string? errorLogged = null;
        int uiRuns = 0;

        var env = new WidgetHostEnvironment(
            onUiThread: a => { uiRuns++; a(); },
            logErrorSink: (m, _) => errorLogged = m,
            saveCalendarCredential: (_, _) => Assert.Fail("Should not be called"));

        env.SaveCalendarCredential("", "secret");
        Assert.AreEqual(0, uiRuns);
        Assert.IsNotNull(errorLogged);

        errorLogged = null;
        env.SaveCalendarCredential("feed", "");
        Assert.AreEqual(0, uiRuns);
        Assert.IsNotNull(errorLogged);
    }
}
