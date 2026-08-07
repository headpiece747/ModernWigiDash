using System.Threading;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class HotkeyButtonWidgetTests
{
    private sealed class FakeContext : IModernWigiDashContext
    {
        public List<string> Errors { get; } = [];
        public List<string> Infos { get; } = [];
        public int Renders { get; private set; }

        public void LogInfo(string message) => Infos.Add(message);
        public void LogError(string message, Exception? ex = null) => Errors.Add(message);
        public void RequestRender() => Renders++;
        public void RequestInspectorRefresh() { }
        public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt) { }
        public void CloseDeviceAuthorization() { }
    }

    private sealed class FakeExecutor
    {
        public int Calls { get; private set; }
        public IReadOnlyList<HotkeyAction>? LastActions { get; private set; }
        public Func<Task>? OnExecute;

        public Task Execute(IReadOnlyList<HotkeyAction> actions, CancellationToken ct)
        {
            Calls++;
            LastActions = actions;
            return OnExecute?.Invoke() ?? Task.CompletedTask;
        }
    }

    private static HotkeyButtonWidget CreatePressedWidget(FakeExecutor executor, FakeContext context)
    {
        var widget = new HotkeyButtonWidget
        {
            ActionType = "Open URL",
            ActionCommand = "https://example.com"
        };
        widget.ActionExecutor = executor.Execute;
        widget.InitializeAsync(context).AsTask().Wait();
        return widget;
    }

    [TestMethod]
    public void OnTouch_TouchUp_ExecutesExactlyOneAction()
    {
        var executor = new FakeExecutor();
        var widget = CreatePressedWidget(executor, new FakeContext());

        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (executor.Calls == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(5);

        Assert.AreEqual(1, executor.Calls);
        Assert.IsNotNull(executor.LastActions);
        Assert.AreEqual(1, executor.LastActions!.Count);
        Assert.AreEqual("https://example.com", executor.LastActions[0].Value);
    }

    [TestMethod]
    public void OnTouch_TouchDown_DoesNotExecute()
    {
        var executor = new FakeExecutor();
        var widget = CreatePressedWidget(executor, new FakeContext());

        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchDown);

        Thread.Sleep(100);
        Assert.AreEqual(0, executor.Calls);
    }

    [TestMethod]
    public void OnTouch_SecondPressWhileInFlight_IsGated()
    {
        var executor = new FakeExecutor();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.OnExecute = () => gate.Task;
        var widget = CreatePressedWidget(executor, new FakeContext());

        // First press blocks on the in-flight executor.
        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (executor.Calls < 1 && DateTime.UtcNow < deadline)
            Thread.Sleep(5);
        Assert.AreEqual(1, executor.Calls);

        // Second press while the first is in flight must not queue another execution.
        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);
        Thread.Sleep(100);
        Assert.AreEqual(1, executor.Calls, "The action gate must block a second press while one is in flight");

        gate.SetResult();
    }

    [TestMethod]
    public void OnTouch_LaunchActionWithEmptyCommand_IsSkippedAndLogged()
    {
        var executor = new FakeExecutor();
        var context = new FakeContext();
        var widget = new HotkeyButtonWidget { ActionType = "Launch App", ActionCommand = "" };
        widget.ActionExecutor = executor.Execute;
        widget.InitializeAsync(context).AsTask().Wait();

        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (context.Errors.Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(5);

        Assert.AreEqual(0, executor.Calls, "An empty launch command must not execute");
        Assert.IsTrue(context.Errors.Any(e => e.Contains("skipped")), "The skip must be surfaced via the context log");
    }

    [TestMethod]
    public void OnTouch_ExecutorFailure_IsLoggedNotThrown()
    {
        var executor = new FakeExecutor();
        executor.OnExecute = () => Task.FromException(new InvalidOperationException("boom"));
        var context = new FakeContext();
        var widget = CreatePressedWidget(executor, context);

        // Must not throw out of OnTouch (the trigger is fire-and-forget).
        widget.OnTouch(new SKPoint(10, 10), TouchEventType.TouchUp);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (context.Errors.Count == 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(5);

        Assert.IsTrue(context.Errors.Any(e => e.Contains("boom")), "Execution failure must surface through the context log");
    }
}
