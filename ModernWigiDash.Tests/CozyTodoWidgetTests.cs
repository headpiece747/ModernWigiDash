namespace ModernWigiDash.Tests;

using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using ModernWigiDash.Widgets;
using ModernWigiDash.Widgets.Todo;

[TestClass]
public class CozyTodoWidgetTests
{
    private static string NewStatePath() => Path.Combine(Path.GetTempPath(), "wmd-todo-test-" + Guid.NewGuid().ToString("N"), "cozy_state.json");

    private static SKCanvas CreateCanvas(int width = 1016, int height = 592)
        => SKSurface.Create(new SKImageInfo(width, height)).Canvas;

    /// <summary>Waits (bounded) until the condition holds: the deterministic
    /// alternative to a fixed sleep racing a fire-and-forget action leg.</summary>
    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition, int timeoutMs = 2000)
    {
        if (await condition().ConfigureAwait(false))
            return true;

        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (await condition().ConfigureAwait(false))
                return true;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return await condition().ConfigureAwait(false);
    }

    [TestMethod]
    public void Metadata_DeclaresCozyTodoIdentity()
    {
        var meta = typeof(CozyTodoWidget).GetCustomAttribute<WidgetMetadataAttribute>();

        Assert.IsNotNull(meta);
        Assert.AreEqual("cozy_todo", meta.Id);
        Assert.AreEqual("To-Do Companion", meta.DisplayName);
        Assert.AreEqual("Productivity", meta.Category);
        Assert.AreEqual(GridSizePreset.Size5x4, meta.DefaultGridSize);
    }

    [TestMethod]
    public void GetEditorKind_TasksJson_ReturnsTodoManagerEditorKind()
    {
        var widget = new CozyTodoWidget();
        var tasksProp = typeof(CozyTodoWidget).GetProperty(nameof(CozyTodoWidget.TasksJson));
        var modeProp = typeof(CozyTodoWidget).GetProperty(nameof(CozyTodoWidget.DisplayMode));

        Assert.IsNotNull(tasksProp);
        Assert.IsNotNull(modeProp);
        Assert.AreEqual(EditorKind.TodoManager, widget.GetEditorKind(tasksProp));
        Assert.IsNull(widget.GetEditorKind(modeProp));
    }

    [TestMethod]
    public async Task InitializeAndRender_RunsCleanlyWithoutThrowing()
    {
        string statePath = NewStatePath();
        var store = new TodoStore(statePath);
        var clock = new FakeTimeProvider();

        var widget = new CozyTodoWidget
        {
            Clock = clock,
            ProviderFactory = _ => new LocalTaskProvider(store, clock),
        };

        var context = new TestContext();
        await widget.InitializeAsync(context);

        var canvas = CreateCanvas(1016, 592);
        widget.Render(canvas, new SKRect(0, 0, 1016, 592));

        Assert.IsTrue(context.Renders >= 1);
        await widget.DisposeAsync();

        if (Directory.Exists(Path.GetDirectoryName(statePath)))
        {
            Directory.Delete(Path.GetDirectoryName(statePath)!, true);
        }
    }


    [TestMethod]
    public async Task Touch_FeedButton_FeedsCompanionAndEarnsAffection()
    {
        string statePath = NewStatePath();
        var store = new TodoStore(statePath);
        store.Write(new TodoDataState
        {
            Companion = new CozyCompanionState
            {
                SnackTreats = 10,
                TotalXp = 0,
            }
        });

        var clock = new FakeTimeProvider();
        var local = new LocalTaskProvider(store, clock);
        var widget = new CozyTodoWidget
        {
            DisplayMode = "Cozy Companion",
            Clock = clock,
            ProviderFactory = _ => local,
        };

        var context = new TestContext();
        await widget.InitializeAsync(context);

        SKRect bounds = new(0, 0, 1016, 592);
        var canvas = CreateCanvas(1016, 592);
        widget.Render(canvas, bounds);

        var geometry = CozyTodoLayout.Compute(bounds, isMinimalist: false, local.CurrentTasks, scrollY: 0f);
        SKPoint feedPoint = new(geometry.FeedButtonRect.MidX, geometry.FeedButtonRect.MidY);

        // The synchronous seam: tap-to-action resolves against the canvas's
        // own frame geometry, no async leg, no sleep.
        var action = widget.HandleTapForTest(feedPoint);
        Assert.AreEqual(CozyTodoHitKind.FeedPet, action.Kind, "the feed button's hit action resolves synchronously");

        // The production path: the touch dispatches the fire-and-forget feed,
        // and the effect lands in the store (a bounded poll, not a sleep race).
        widget.OnTouch(feedPoint, TouchEventType.TouchDown);
        widget.OnTouch(feedPoint, TouchEventType.TouchUp);

        bool fed = await WaitForAsync(async () =>
        {
            var state = local.GetCompanionState();
            return state.SnackTreats == 5 && state.TotalXp == 50;
        }).ConfigureAwait(false);
        Assert.IsTrue(fed, "the feed effect must land in the store");

        var companion = local.GetCompanionState();
        Assert.AreEqual(5, companion.SnackTreats, "Feeding consumes 5 treats");
        Assert.AreEqual(50, companion.TotalXp, "Feeding grants 50 affection XP");

        await widget.DisposeAsync().ConfigureAwait(false);

        if (Directory.Exists(Path.GetDirectoryName(statePath)))
        {
            Directory.Delete(Path.GetDirectoryName(statePath)!, true);
        }
    }

    [TestMethod]
    public async Task HandleTap_TaskRow_ResolvesTheToggleAction()
    {
        string statePath = NewStatePath();
        var store = new TodoStore(statePath);
        store.Write(new TodoDataState
        {
            Tasks = [new TodoItem("task-sample-1", "Sample task", false, "Test")]
        });

        var clock = new FakeTimeProvider();
        var local = new LocalTaskProvider(store, clock);
        var widget = new CozyTodoWidget
        {
            DisplayMode = "Cozy Companion",
            Clock = clock,
            ProviderFactory = _ => local,
        };

        var context = new TestContext();
        await widget.InitializeAsync(context);
        await widget.RefreshForTest(); // prime the published snapshot (the canvas's task rows come from it)

        SKRect bounds = new(0, 0, 1016, 592);
        var canvas = CreateCanvas(1016, 592);
        widget.Render(canvas, bounds);

        var geometry = CozyTodoLayout.Compute(bounds, isMinimalist: false, local.CurrentTasks, scrollY: 0f);
        var row = geometry.TaskRows[0];
        SKPoint checkPoint = new(row.RowRect.MidX, row.RowRect.MidY);

        var action = widget.HandleTapForTest(checkPoint);

        Assert.AreEqual(CozyTodoHitKind.ToggleTask, action.Kind, "the task row's hit action is the toggle");
        Assert.AreEqual("task-sample-1", action.TaskId, "the toggle names the row's task");

        await widget.DisposeAsync();
        if (Directory.Exists(Path.GetDirectoryName(statePath)))
        {
            Directory.Delete(Path.GetDirectoryName(statePath)!, true);
        }
    }

    [TestMethod]
    public async Task Touch_TaskRow_TogglesCompletion()
    {
        string statePath = NewStatePath();
        var store = new TodoStore(statePath);
        store.Write(new TodoDataState
        {
            Tasks = [new TodoItem("task-sample-1", "Sample task", false, "Test")]
        });

        var clock = new FakeTimeProvider();
        var local = new LocalTaskProvider(store, clock);
        var widget = new CozyTodoWidget
        {
            DisplayMode = "Cozy Companion",
            Clock = clock,
            ProviderFactory = _ => local,
        };

        var context = new TestContext();
        await widget.InitializeAsync(context);
        await widget.RefreshForTest(); // prime the published snapshot (the canvas's task rows come from it)

        SKRect bounds = new(0, 0, 1016, 592);
        var canvas = CreateCanvas(1016, 592);
        widget.Render(canvas, bounds);

        var geometry = CozyTodoLayout.Compute(bounds, isMinimalist: false, local.CurrentTasks, scrollY: 0f);
        var row = geometry.TaskRows[0];
        SKPoint checkPoint = new(row.RowRect.MidX, row.RowRect.MidY);

        widget.OnTouch(checkPoint, TouchEventType.TouchDown);
        widget.OnTouch(checkPoint, TouchEventType.TouchUp);

        bool toggled = await WaitForAsync(async () =>
        {
            var tasks = await local.GetTasksAsync().ConfigureAwait(false);
            return tasks.Count == 1 && tasks[0].IsDone;
        }).ConfigureAwait(false);
        Assert.IsTrue(toggled, "the toggle effect must land in the store");

        var tasks = await local.GetTasksAsync().ConfigureAwait(false);
        Assert.IsTrue(tasks[0].IsDone, "Task should be toggled to completed");

        await widget.DisposeAsync().ConfigureAwait(false);

        if (Directory.Exists(Path.GetDirectoryName(statePath)))
        {
            Directory.Delete(Path.GetDirectoryName(statePath)!, true);
        }
    }

    [TestMethod]
    public async Task CleanMinimalistMode_RendersWithoutException()
    {
        string statePath = NewStatePath();
        var store = new TodoStore(statePath);
        var clock = new FakeTimeProvider();

        var widget = new CozyTodoWidget
        {
            Clock = clock,
            DisplayMode = "Clean Minimalist",
            ProviderFactory = _ => new LocalTaskProvider(store, clock),
        };

        var context = new TestContext();
        await widget.InitializeAsync(context);

        var canvas = CreateCanvas(812, 296);
        widget.Render(canvas, new SKRect(0, 0, 812, 296));

        Assert.IsTrue(context.Renders >= 1);
        await widget.DisposeAsync();

        if (Directory.Exists(Path.GetDirectoryName(statePath)))
        {
            Directory.Delete(Path.GetDirectoryName(statePath)!, true);
        }
    }

    [TestMethod]
    public async Task OnPropertyChanged_TasksJsonCleared_DoesNotPropagateToProvider_TheTypedFunnelIsTheWritePath()
    {
        string statePath = NewStatePath();
        var store = new TodoStore(statePath);
        var clock = new FakeTimeProvider();
        var local = new LocalTaskProvider(store, clock);
        local.AddTask(new TodoItem("t-1", "Initial task", false));

        var widget = new CozyTodoWidget
        {
            Clock = clock,
            ProviderFactory = _ => local,
        };

        var context = new TestContext();
        await widget.InitializeAsync(context);

        Assert.AreEqual(1, local.CurrentTasks.Count);

        // Clearing the profile cache never propagates to a provider:
        // TasksJson is a one-directional cache, so a cache clear is cache
        // bookkeeping only.
        widget.TasksJson = "[]";
        widget.OnPropertyChanged(nameof(CozyTodoWidget.TasksJson), "[]");
        var tasksAfterCacheClear = await local.GetTasksAsync();
        Assert.AreEqual(1, tasksAfterCacheClear.Count, "clearing the TasksJson cache must not write through to the provider");

        // The typed commit funnel is the one task-write path: one commit
        // clears the store owner and writes the cache.
        widget.CommitTasks("[]");
        var tasksAfterCommit = await local.GetTasksAsync();
        Assert.AreEqual(0, tasksAfterCommit.Count, "the typed commit funnel is the only task-write path");
        Assert.AreEqual("[]", widget.TasksJson, "the commit funnel writes the profile cache one-directionally");

        await widget.DisposeAsync();
        if (Directory.Exists(Path.GetDirectoryName(statePath)))
        {
            Directory.Delete(Path.GetDirectoryName(statePath)!, true);
        }
    }

    [TestMethod]
    public async Task CommitTasks_WritesTheProviderAndTheCacheFromThePersistedString()
    {
        string statePath = NewStatePath();
        var store = new TodoStore(statePath);
        var clock = new FakeTimeProvider();
        var local = new LocalTaskProvider(store, clock);

        var widget = new CozyTodoWidget
        {
            Clock = clock,
            ProviderFactory = _ => local,
        };

        var context = new TestContext();
        await widget.InitializeAsync(context);

        IReadOnlyList<TodoItem> items = [new TodoItem("t-1", "From funnel", false)];
        string json = TodoItemCodec.Serialize(items);
        widget.CommitTasks(json);

        Assert.AreEqual(json, widget.TasksJson, "the funnel writes the profile cache with the same string");
        var tasks = await local.GetTasksAsync();
        Assert.AreEqual(1, tasks.Count, "the provider write landed before the funnel returned (provider-first ordering)");
        Assert.AreEqual("t-1", tasks[0].Id);

        await widget.DisposeAsync();
        if (Directory.Exists(Path.GetDirectoryName(statePath)))
        {
            Directory.Delete(Path.GetDirectoryName(statePath)!, true);
        }
    }

    [TestMethod]
    public async Task Touch_ConcurrentTaskToggles_MaintainsConsistency()
    {
        string statePath = NewStatePath();
        var store = new TodoStore(statePath);
        var clock = new FakeTimeProvider();
        var local = new LocalTaskProvider(store, clock);
        local.AddTask(new TodoItem("t-1", "Task 1", false));
        local.AddTask(new TodoItem("t-2", "Task 2", false));
        local.AddTask(new TodoItem("t-3", "Task 3", false));

        var widget = new CozyTodoWidget
        {
            DisplayMode = "Cozy Companion",
            Clock = clock,
            ProviderFactory = _ => local,
        };

        var context = new TestContext();
        await widget.InitializeAsync(context);
        await widget.RefreshForTest();

        SKRect bounds = new(0, 0, 1016, 592);
        var canvas = CreateCanvas(1016, 592);
        widget.Render(canvas, bounds);

        var geometry = CozyTodoLayout.Compute(bounds, isMinimalist: false, local.CurrentTasks, scrollY: 0f);

        // Concurrently touch all 3 tasks
        List<Task> touches = [];
        for (int i = 0; i < 3; i++)
        {
            var row = geometry.TaskRows[i];
            SKPoint pt = new(row.RowRect.MidX, row.RowRect.MidY);
            touches.Add(Task.Run(() =>
            {
                widget.OnTouch(pt, TouchEventType.TouchDown);
                widget.OnTouch(pt, TouchEventType.TouchUp);
            }));
        }

        await Task.WhenAll(touches);
        // The three fire-and-forget toggles each take the store's process
        // gate for a file write; the 10 s budget (vs the 2 s default) is the
        // room that leg needs under test-process load. The race detection
        // stands: a toggle that never lands still fails.
        bool allDone = await WaitForAsync(async () =>
        {
            var done = await local.GetTasksAsync().ConfigureAwait(false);
            return done.Count == 3 && done.All(t => t.IsDone);
        }, timeoutMs: 10000).ConfigureAwait(false);
        Assert.IsTrue(allDone, "all concurrent toggles must land in the store");

        var tasks = await local.GetTasksAsync().ConfigureAwait(false);
        Assert.IsTrue(tasks.All(t => t.IsDone), "All tasks should be marked done without race conditions");

        await widget.DisposeAsync().ConfigureAwait(false);
        if (Directory.Exists(Path.GetDirectoryName(statePath)))
        {
            Directory.Delete(Path.GetDirectoryName(statePath)!, true);
        }
    }

    [TestMethod]
    public async Task InspectorAction_FeedPet_InvokesFeedAndUpdatesState()
    {
        string statePath = NewStatePath();
        var store = new TodoStore(statePath);
        store.Write(new TodoDataState
        {
            Companion = new CozyCompanionState { SnackTreats = 10, TotalXp = 0 }
        });

        var clock = new FakeTimeProvider();
        var local = new LocalTaskProvider(store, clock);
        var widget = new CozyTodoWidget
        {
            Clock = clock,
            ProviderFactory = _ => local,
        };

        var context = new TestContext();
        await widget.InitializeAsync(context);
        // Prime the published snapshot synchronously: the action's active
        // state reads the published box, and the background loop's first
        // tick is cadence-timed (the assertion must not race it).
        await widget.RefreshForTest().ConfigureAwait(false);

        Assert.IsTrue(widget.IsWidgetActionActive(nameof(CozyTodoWidget.FeedPetAction)));
        string? label = widget.GetWidgetActionLabel(nameof(CozyTodoWidget.FeedPetAction));
        Assert.IsNotNull(label);
        Assert.IsTrue(label.Contains("10 treats"));

        widget.InvokeWidgetAction(nameof(CozyTodoWidget.FeedPetAction));
        bool fed = await WaitForAsync(async () =>
        {
            var state = local.GetCompanionState();
            return state.SnackTreats == 5 && state.TotalXp == 50;
        }).ConfigureAwait(false);
        Assert.IsTrue(fed, "the inspector feed action's effect must land in the store");

        var state = local.GetCompanionState();
        Assert.AreEqual(5, state.SnackTreats);
        Assert.AreEqual(50, state.TotalXp);

        await widget.DisposeAsync().ConfigureAwait(false);
        if (Directory.Exists(Path.GetDirectoryName(statePath)))
        {
            Directory.Delete(Path.GetDirectoryName(statePath)!, true);
        }
    }

    [TestMethod]
    public void VerifyAllMoodSprites_EmbeddedAndDecodeValidly()
    {
        string[] spriteNames =
        [
            "dragon_companion.png",
            "dragon_focus.png",
            "dragon_overjoyed.png",
            "dragon_overjoyed_down.png",
            "dragon_sleepy.png",
        ];

        var asm = typeof(CozyTodoRenderer).Assembly;
        foreach (string name in spriteNames)
        {
            using var stream = asm.GetManifestResourceStream($"ModernWigiDash.Widgets.Resources.{name}");
            Assert.IsNotNull(stream, $"Embedded resource ModernWigiDash.Widgets.Resources.{name} must exist");
            using var bmp = SKBitmap.Decode(stream);
            Assert.IsNotNull(bmp, $"Bitmap for {name} must decode successfully");
            Assert.IsTrue(bmp.Width > 100 && bmp.Height > 100, $"Bitmap {name} must have valid dimensions (got {bmp.Width}x{bmp.Height})");
        }
    }

}
