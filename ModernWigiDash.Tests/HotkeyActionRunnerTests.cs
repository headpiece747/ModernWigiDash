namespace ModernWigiDash.Tests;

/// <summary>
/// The hotkey action-fire ritual pinned directly: the page-flip routing to
/// the context seam, the empty-command skip (needs-command types log and
/// never reach the executor), the AHK script launch, the executor seam for
/// every other action kind, the saturated-gate drop (a concurrent fire is
/// dropped, not queued), the null-context degrade, and the teardown
/// cancellation of an in-flight run. All drivable through the four
/// provider seams, with no widget and no WPF surface.
/// </summary>
[TestClass]
public class HotkeyActionRunnerTests
{
    private static HotkeyActionRunner CreateRunner(
        HotkeyAction action,
        string actionType,
        IModernWigiDashContext? context,
        Func<Func<IReadOnlyList<HotkeyAction>, CancellationToken, Task>?> executorProvider)
        => new(
            () => action,
            () => actionType,
            () => context,
            executorProvider);

    [TestMethod]
    public async Task RunAsync_PageNavigateAction_RoutesToTheContextInsteadOfTheExecutor()
    {
        var context = new TestContext();
        int executorCalls = 0;
        var runner = CreateRunner(
            new() { Kind = HotkeyActionKind.PageNavigate, Value = "1" },
            "Next Page",
            context,
            () => (actions, token) =>
            {
                executorCalls++;
                return Task.CompletedTask;
            });

        await runner.RunAsync().ConfigureAwait(false);

        Assert.AreEqual(1, context.NavigatePageCalls.Count);
        Assert.AreEqual(1, context.NavigatePageCalls[0]);
        Assert.AreEqual(1, context.Renders);
        Assert.AreEqual(0, executorCalls, "a page flip never reaches the executor");
        await runner.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RunAsync_EmptyCommandOnANeedsCommandType_LogsAndSkips()
    {
        var context = new TestContext();
        int executorCalls = 0;
        var runner = CreateRunner(
            new() { Kind = HotkeyActionKind.Launch, Value = "" },
            "Launch App",
            context,
            () => (actions, token) =>
            {
                executorCalls++;
                return Task.CompletedTask;
            });

        await runner.RunAsync().ConfigureAwait(false);

        Assert.AreEqual(0, executorCalls, "an empty command never reaches the executor");
        Assert.AreEqual(1, context.Errors.Count);
        Assert.AreEqual(0, context.Renders);
        await runner.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RunAsync_AhkScriptAction_LaunchesTheScriptThroughTheContext()
    {
        var context = new TestContext();
        int executorCalls = 0;
        var runner = CreateRunner(
            new() { Kind = HotkeyActionKind.AhkScript, Value = "C:\\scripts\\x.ahk" },
            "Run AHK Script",
            context,
            () => (actions, token) =>
            {
                executorCalls++;
                return Task.CompletedTask;
            });

        await runner.RunAsync().ConfigureAwait(false);

        Assert.AreEqual(1, context.AhkScriptCalls.Count);
        Assert.AreEqual("C:\\scripts\\x.ahk", context.AhkScriptCalls[0]);
        Assert.AreEqual(1, context.Renders);
        Assert.AreEqual(0, executorCalls, "an AHK script launch never reaches the executor");
        await runner.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RunAsync_OtherAction_ExecutesThroughTheExecutorSeam()
    {
        var context = new TestContext();
        List<HotkeyAction> seen = [];
        var runner = CreateRunner(
            new() { Kind = HotkeyActionKind.Launch, Value = "C:\\app\\app.exe" },
            "Launch App",
            context,
            () => (actions, token) =>
            {
                seen.AddRange(actions);
                return Task.CompletedTask;
            });

        await runner.RunAsync().ConfigureAwait(false);

        Assert.AreEqual(1, seen.Count);
        Assert.AreEqual(HotkeyActionKind.Launch, seen[0].Kind);
        Assert.AreEqual("C:\\app\\app.exe", seen[0].Value);
        Assert.AreEqual(1, context.Renders);
        await runner.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RunAsync_SaturatedGate_DropsTheConcurrentFire()
    {
        var context = new TestContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int executorCalls = 0;
        var runner = CreateRunner(
            new() { Kind = HotkeyActionKind.Launch, Value = "C:\\app\\app.exe" },
            "Launch App",
            context,
            () => (actions, token) =>
            {
                executorCalls++;
                entered.TrySetResult();
                return release.Task;
            });

        var first = runner.RunAsync();
        await entered.Task.ConfigureAwait(false);

        var second = runner.RunAsync();
        await second.ConfigureAwait(false);
        Assert.AreEqual(1, executorCalls, "a saturated gate drops the concurrent fire, it never queues");

        release.SetResult();
        await first.ConfigureAwait(false);
        Assert.AreEqual(1, executorCalls, "the dropped fire never reached the executor");
        await runner.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RunAsync_NullContext_StillExecutesTheAction()
    {
        int executorCalls = 0;
        var runner = CreateRunner(
            new() { Kind = HotkeyActionKind.Launch, Value = "C:\\app\\app.exe" },
            "Launch App",
            null,
            () => (actions, token) =>
            {
                executorCalls++;
                return Task.CompletedTask;
            });

        await runner.RunAsync().ConfigureAwait(false);

        Assert.AreEqual(1, executorCalls, "a missing context degrades to a bare execute, never a no-op");
        await runner.DisposeAsync().ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DisposeAsync_CancelsTheInFlightRun()
    {
        var context = new TestContext();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(
            new() { Kind = HotkeyActionKind.Launch, Value = "C:\\app\\app.exe" },
            "Launch App",
            context,
            () => (actions, token) =>
            {
                entered.TrySetResult();
                return Task.Delay(10_000, token);
            });

        var run = runner.RunAsync();
        await entered.Task.ConfigureAwait(false);

        await runner.DisposeAsync().ConfigureAwait(false);
        await run.ConfigureAwait(false);

        Assert.AreEqual(1, context.Infos.Count);
        StringAssert.Contains(context.Infos[0], "cancelled");
        Assert.AreEqual(0, context.Errors.Count);
    }
}
