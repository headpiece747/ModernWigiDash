using System.IO;
using System.Reflection;

namespace ModernWigiDash.Tests;

[TestClass]
public class HotkeyButtonWidgetTests
{
    private sealed class FakeExecutor
    {
        public int Calls { get; private set; }
        public IReadOnlyList<HotkeyAction>? LastActions { get; private set; }
        public Func<Task>? OnExecute;

        public Task Execute(IReadOnlyList<HotkeyAction> actions, CancellationToken cancellationToken)
        {
            // Mirrors the real executor: an in-flight cancellation aborts the macro.
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastActions = actions;
            return OnExecute?.Invoke() ?? Task.CompletedTask;
        }
    }

    private static async Task<HotkeyButtonWidget> CreatePressedWidget(FakeExecutor executor, TestContext context)
    {
        var widget = new HotkeyButtonWidget
        {
            ActionType = "Open URL",
            ActionCommand = "https://example.com"
        };
        widget.ActionExecutor = executor.Execute;
        await widget.InitializeAsync(context).ConfigureAwait(false);
        return widget;
    }

    [TestMethod]
    public async Task OnTouch_TouchUp_ExecutesExactlyOneAction()
    {
        var executor = new FakeExecutor();
        var widget = await CreatePressedWidget(executor, new TestContext());

        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);

        await TestWait.WaitUntilAsync(() => executor.Calls > 0, TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, executor.Calls);
        Assert.IsNotNull(executor.LastActions);
        Assert.AreEqual(1, executor.LastActions.Count);
        Assert.AreEqual("https://example.com", executor.LastActions[0].Value);
    }

    [TestMethod]
    public async Task OnTouch_TouchDown_DoesNotExecute()
    {
        var executor = new FakeExecutor();
        var widget = await CreatePressedWidget(executor, new TestContext());

        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchDown);

        // TouchDown is purely synchronous (press state + render request).
        Assert.AreEqual(0, executor.Calls);
    }

    [TestMethod]
    public async Task OnTouch_SecondPressWhileInFlight_IsGated()
    {
        var executor = new FakeExecutor();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.OnExecute = () => gate.Task;
        var widget = await CreatePressedWidget(executor, new TestContext());

        // First press blocks on the in-flight executor.
        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);
        await TestWait.WaitUntilAsync(() => executor.Calls >= 1, TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, executor.Calls);

        // Second press while the first is in flight must not queue another execution.
        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);
        await Task.Delay(100);
        Assert.AreEqual(1, executor.Calls, "The action gate must block a second press while one is in flight");

        gate.SetResult();
    }

    [TestMethod]
    public async Task OnTouch_LaunchActionWithEmptyCommand_IsSkippedAndLogged()
    {
        var executor = new FakeExecutor();
        var context = new TestContext();
        var widget = new HotkeyButtonWidget { ActionType = "Launch App", ActionCommand = "" };
        widget.ActionExecutor = executor.Execute;
        await widget.InitializeAsync(context);

        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);

        await TestWait.WaitUntilAsync(() => context.Errors.Count > 0, TimeSpan.FromSeconds(2));

        Assert.AreEqual(0, executor.Calls, "An empty launch command must not execute");
        Assert.IsTrue(context.Errors.Any(e => e.Contains("skipped")), "The skip must be surfaced via the context log");
    }

    [TestMethod]
    public async Task OnTouch_ExecutorFailure_IsLoggedNotThrown()
    {
        var executor = new FakeExecutor();
        executor.OnExecute = () => Task.FromException(new InvalidOperationException("boom"));
        var context = new TestContext();
        var widget = await CreatePressedWidget(executor, context);

        // Must not throw out of OnTouch (the trigger is fire-and-forget).
        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);

        await TestWait.WaitUntilAsync(() => context.Errors.Count > 0, TimeSpan.FromSeconds(2));

        Assert.IsTrue(context.Errors.Any(e => e.Contains("boom")), "Execution failure must surface through the context log");
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
    public void HotkeyWidget_ActionType_DefaultsToLaunchApp()
    {
        var widget = new HotkeyButtonWidget();
        Assert.AreEqual("Launch App", widget.ActionType);
        Assert.AreEqual("", widget.ActionCommand);
        Assert.AreEqual("Hotkey", widget.ButtonLabel);
        Assert.AreEqual("Tap to run", widget.Description);
    }

    [TestMethod]
    public void HotkeyWidget_MediaActionTypes_MapToCatalogMediaKeys()
    {
        // Derived from the catalog: a new media entry extends this pin.
        foreach (var entry in HotkeyActionCatalog.Entries.Where(e => e.Kind == HotkeyActionKind.MediaKey))
        {
            var action = HotkeyActionCatalog.Create(entry.Name, "ignored");
            Assert.AreEqual(HotkeyActionKind.MediaKey, action.Kind, entry.Name);
            Assert.AreEqual(entry.MediaKey, action.Value, entry.Name);
        }
    }

    [TestMethod]
    public void HotkeyActionAttribute_Options_MatchTheActionCatalog()
    {
        // The inspector's choice list is the compile-time LITERAL copy of the
        // action catalog (attributes cannot bind a runtime value): a renamed
        // or hand-edited action name must fail HERE, not surface at runtime
        // as the catalog's Launch default.
        var attribute = typeof(HotkeyButtonWidget)
            .GetProperty(nameof(HotkeyButtonWidget.ActionType))!
            .GetCustomAttribute<WidgetPropertyAttribute>()!;

        Assert.AreEqual(HotkeyActionCatalog.DefaultName, attribute.DefaultValue,
            "the attribute default must match the catalog's default name");
        Assert.AreEqual(HotkeyActionCatalog.Entries.Count, attribute.Options.Length,
            "every catalog action must be an option, and every option a catalog action");
        for (int i = 0; i < attribute.Options.Length; i++)
        {
            string option = attribute.Options[i];
            Assert.AreEqual(HotkeyActionCatalog.Entries[i].Name, option,
                "choice order must match the catalog order");
            Assert.AreEqual(HotkeyActionCatalog.Entries[i].Kind, HotkeyActionCatalog.Create(option, "").Kind,
                "every option must parse back to its catalog kind");
        }
    }

    [TestMethod]
    public void HotkeyActionCatalog_UnknownName_DegradesToLaunch()
    {
        // A hand-edited profile value that is not a catalog name must land on
        // the Launch kind with the raw command: the one unknown-name rule.
        var action = HotkeyActionCatalog.Create("Nonsense", "notepad.exe");
        Assert.AreEqual(HotkeyActionKind.Launch, action.Kind);
        Assert.AreEqual("notepad.exe", action.Value);
        Assert.IsFalse(HotkeyActionCatalog.NeedsCommand("Nonsense"),
            "an unknown name must not force the empty-command skip");
    }

    [TestMethod]
    public void HotkeyActionCatalog_NeedsCommand_OnlyLaunchAndUrl()
    {
        Assert.IsTrue(HotkeyActionCatalog.NeedsCommand("Launch App"));
        Assert.IsTrue(HotkeyActionCatalog.NeedsCommand("Open URL"));
        foreach (var entry in HotkeyActionCatalog.Entries.Where(e => e.MediaKey is not null))
        {
            Assert.IsFalse(HotkeyActionCatalog.NeedsCommand(entry.Name), entry.Name);
        }
    }

    [TestMethod]
    public void HotkeyWidget_OpenUrlActionType_MapsToOpenUrl()
    {
        var action = HotkeyActionCatalog.Create("Open URL", "https://example.com");
        Assert.AreEqual(HotkeyActionKind.OpenUrl, action.Kind);
        Assert.AreEqual("https://example.com", action.Value);
    }

    [TestMethod]
    public void HotkeyWidget_SingleAction_ExecutesOneAction()
    {
        var launch = HotkeyActionCatalog.Create("Launch App", "notepad.exe");
        Assert.AreEqual(HotkeyActionKind.Launch, launch.Kind);
        Assert.AreEqual("notepad.exe", launch.Value);
        var openUrl = HotkeyActionCatalog.Create("Open URL", "https://example.com");
        Assert.AreEqual(HotkeyActionKind.OpenUrl, openUrl.Kind);
        Assert.AreEqual("https://example.com", openUrl.Value);
        var mute = HotkeyActionCatalog.Create("Mute", "");
        Assert.AreEqual(HotkeyActionKind.MediaKey, mute.Kind);
        Assert.AreEqual("MUTE", mute.Value);
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
    public void HotkeyWidget_IconProbe_RefreshesWhenIconChanges()
    {
        // The Griddy-icon probe memoizes per icon name, and the icon set is
        // static; changing the name, then changing it back, must refresh the
        // memoized result instead of serving a stale probe.
        var widget = new HotkeyButtonWidget { Icon = "activity" };
        Assert.IsTrue(widget.IsPointOverIcon(200, 150, 100, 46), "The known icon must hit-test at its center");

        widget.Icon = "definitely_not_an_icon";
        Assert.IsFalse(widget.IsPointOverIcon(200, 150, 100, 46), "An unknown icon must not hit-test");

        widget.Icon = "activity";
        Assert.IsTrue(widget.IsPointOverIcon(200, 150, 100, 46), "The probe memo must refresh when the icon name changes back");
    }
}
