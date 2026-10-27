using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModernWigiDash.App;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

[TestClass]
public class ModernWigiDashHostContextTests
{
    private sealed class FakeWidget : ModernWidgetBase, IGlobalHotkeyProvider
    {
        public override void Render(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect bounds) { }
        public bool AffectsGlobalHotkey(string propertyName) => propertyName == "HotkeyChord";
        public bool TryGetGlobalHotkey(out int modFlags, out ushort virtualKey, out string chord)
        {
            modFlags = 0;
            virtualKey = 0;
            chord = "";
            return false;
        }
        public void FireGlobalHotkey() { }
    }

    [TestMethod]
    public void LogInfo_RoutesToInjectedSink()
    {
        string? logged = null;
        var context = new ModernWigiDashHostContext(logInfoSink: msg => logged = msg);

        context.LogInfo("Hello Host");

        Assert.AreEqual("Hello Host", logged);
    }

    [TestMethod]
    public void LogError_RoutesToInjectedSink()
    {
        string? loggedMsg = null;
        Exception? loggedEx = null;
        var ex = new InvalidOperationException("Boom");
        var context = new ModernWigiDashHostContext(logErrorSink: (msg, e) => { loggedMsg = msg; loggedEx = e; });

        context.LogError("Fail", ex);

        Assert.AreEqual("Fail", loggedMsg);
        Assert.AreSame(ex, loggedEx);
    }

    [TestMethod]
    public void RequestRender_DispatchesOnUiThread()
    {
        int uiThreadRuns = 0;
        int renderRequests = 0;
        var context = new ModernWigiDashHostContext(
            onUiThread: action => { uiThreadRuns++; action(); },
            requestRender: () => renderRequests++);

        context.RequestRender();

        Assert.AreEqual(1, uiThreadRuns);
        Assert.AreEqual(1, renderRequests);
    }

    [TestMethod]
    public void RequestInspectorRefresh_DispatchesOnUiThread()
    {
        int refreshes = 0;
        var context = new ModernWigiDashHostContext(
            requestInspectorRefresh: () => refreshes++);

        context.RequestInspectorRefresh();

        Assert.AreEqual(1, refreshes);
    }

    [TestMethod]
    public void ShowAndCloseDeviceAuthorization_DispatchesOnUiThread()
    {
        string? shownService = null;
        bool closed = false;
        var uri = new Uri("https://auth.example.com");

        var context = new ModernWigiDashHostContext(
            showDeviceAuth: (service, _, _, _) => shownService = service,
            closeDeviceAuth: () => closed = true);

        context.ShowDeviceAuthorization("Twitch", uri, "CODE123", DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.AreEqual("Twitch", shownService);

        context.CloseDeviceAuthorization();
        Assert.IsTrue(closed);
    }

    [TestMethod]
    public void PersistProperty_UpdatesPlacedWidget_AndMarksDirty()
    {
        var widgetInstance = new FakeWidget();
        var placed = new PlacedWidgetInstance
        {
            PluginId = "fake",
            ActiveInstance = widgetInstance
        };
        var profile = new ProfileLayout
        {
            Pages = [new PageLayout { Widgets = [placed] }]
        };

        bool dirty = false;
        bool hotkeyRefreshed = false;

        var context = new ModernWigiDashHostContext(
            profileProvider: () => profile,
            markProfileDirty: () => dirty = true,
            refreshGlobalHotkeys: () => hotkeyRefreshed = true);

        context.PersistProperty(widgetInstance, "Title", "My Widget");

        Assert.AreEqual("My Widget", placed.PropertyValues["Title"]);
        Assert.IsTrue(dirty);
        Assert.IsFalse(hotkeyRefreshed);

        // When property affects global hotkey
        context.PersistProperty(widgetInstance, "HotkeyChord", "Ctrl+Alt+F");
        Assert.IsTrue(hotkeyRefreshed);
    }

    [TestMethod]
    public void NavigatePage_DispatchesOnUiThread_AndIgnoresZeroDelta()
    {
        int navigatedDelta = 0;
        var context = new ModernWigiDashHostContext(
            navigatePage: delta => navigatedDelta += delta);

        context.NavigatePage(0);
        Assert.AreEqual(0, navigatedDelta, "Delta 0 must be ignored");

        context.NavigatePage(1);
        Assert.AreEqual(1, navigatedDelta);

        context.NavigatePage(-1);
        Assert.AreEqual(0, navigatedDelta);
    }

    [TestMethod]
    public void LaunchAutoHotkeyScript_InvokesLauncher()
    {
        string? launchedPath = null;
        var context = new ModernWigiDashHostContext(
            launchAhkScript: path => launchedPath = path);

        context.LaunchAutoHotkeyScript(@"C:\scripts\macro.ahk");

        Assert.AreEqual(@"C:\scripts\macro.ahk", launchedPath);
    }

    [TestMethod]
    public void SaveCalendarCredential_ValidCredentials_DispatchesOnUiThread()
    {
        string? savedFeed = null;
        string? savedPass = null;
        var context = new ModernWigiDashHostContext(
            saveCalendarCredential: (f, p) => { savedFeed = f; savedPass = p; });

        context.SaveCalendarCredential("work_cal", "secret123");

        Assert.AreEqual("work_cal", savedFeed);
        Assert.AreEqual("secret123", savedPass);
    }

    [TestMethod]
    public void SaveCalendarCredential_BlankCredentials_LogsErrorAndSkips()
    {
        string? error = null;
        bool saved = false;
        var context = new ModernWigiDashHostContext(
            saveCalendarCredential: (_, _) => saved = true,
            logErrorSink: (msg, _) => error = msg);

        context.SaveCalendarCredential("", "secret");

        Assert.IsFalse(saved);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "blank");
    }

    [TestMethod]
    public void TryReadSensors_And_TryReadFrameTime_ReadsFromInjectedProviders()
    {
        var sensorSnapshot = new SensorSnapshotDto { IsConnected = true };
        var frameTimeSnapshot = new FrameTimeSnapshotDto { IsAvailable = true };

        var context = new ModernWigiDashHostContext(
            sensorProvider: () => sensorSnapshot,
            frameTimeProvider: () => frameTimeSnapshot);

        var sensors = context.TryReadSensors();
        var frameTime = context.TryReadFrameTime();

        Assert.AreSame(sensorSnapshot, sensors);
        Assert.AreSame(frameTimeSnapshot, frameTime);
    }
}
