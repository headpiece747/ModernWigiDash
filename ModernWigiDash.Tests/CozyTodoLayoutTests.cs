namespace ModernWigiDash.Tests;

using ModernWigiDash.Widgets.Todo;

[TestClass]
public class CozyTodoLayoutTests
{
    private static readonly List<TodoItem> SampleTasks =
    [
        new("t-1", "Design widget layout", true, "Design"),
        new("t-2", "Implement SkiaSharp renderer", false, "Code"),
        new("t-3", "Write unit test suite", false, "Test"),
    ];

    [TestMethod]
    public void Compute_WideLayout5x4_ProducesSideBySideSanctuaryAndTasks()
    {
        SKRect bounds = new(0, 0, 1016, 592);
        var layout = CozyTodoLayout.Compute(bounds, isMinimalist: false, SampleTasks, scrollY: 0f);

        Assert.IsFalse(layout.IsMinimalist);
        Assert.IsFalse(layout.CompanionCardRect.IsEmpty);
        Assert.IsFalse(layout.TasksPanelRect.IsEmpty);
        Assert.IsTrue(layout.CompanionCardRect.Right <= layout.TasksPanelRect.Left, "Sanctuary should be to the left of the tasks panel");

        Assert.IsFalse(layout.PetSpriteRect.IsEmpty);
        Assert.IsTrue(layout.CompanionCardRect.Contains(layout.PetSpriteRect.MidX, layout.PetSpriteRect.MidY));

        Assert.IsFalse(layout.FeedButtonRect.IsEmpty);
        Assert.IsTrue(layout.CompanionCardRect.Contains(layout.FeedButtonRect.MidX, layout.FeedButtonRect.MidY));

        Assert.IsFalse(layout.PomodoroButtonRect.IsEmpty);

        Assert.AreEqual(3, layout.TaskRows.Count);
        Assert.AreEqual("t-1", layout.TaskRows[0].TaskId);
        Assert.IsTrue(layout.TaskRows[0].IsDone);
    }

    [TestMethod]
    public void Compute_SplitBanner4x2_ProducesWideCompanionAndTasks()
    {
        SKRect bounds = new(0, 0, 812, 296);
        var layout = CozyTodoLayout.Compute(bounds, isMinimalist: false, SampleTasks, scrollY: 0f);

        Assert.IsFalse(layout.IsMinimalist);
        Assert.IsFalse(layout.CompanionCardRect.IsEmpty);
        Assert.IsFalse(layout.TasksPanelRect.IsEmpty);
        Assert.IsTrue(layout.CompanionCardRect.Right <= layout.TasksPanelRect.Left);
    }

    [TestMethod]
    public void Compute_CompactPoster2x3_ProducesTopCompanionAndBottomTasks()
    {
        SKRect bounds = new(0, 0, 406, 444);
        var layout = CozyTodoLayout.Compute(bounds, isMinimalist: false, SampleTasks, scrollY: 0f);

        Assert.IsFalse(layout.IsMinimalist);
        Assert.IsFalse(layout.CompanionCardRect.IsEmpty);
        Assert.IsFalse(layout.TasksPanelRect.IsEmpty);
        Assert.IsTrue(layout.CompanionCardRect.Bottom <= layout.TasksPanelRect.Top, "In tall layout, sanctuary should be stacked above tasks panel");
    }

    [TestMethod]
    public void Compute_MinimalistMode_HidesCompanionAndExpandsTasks()
    {
        SKRect bounds = new(0, 0, 1016, 592);
        var layout = CozyTodoLayout.Compute(bounds, isMinimalist: true, SampleTasks, scrollY: 0f);

        Assert.IsTrue(layout.IsMinimalist);
        Assert.IsTrue(layout.CompanionCardRect.IsEmpty);
        Assert.IsTrue(layout.PetSpriteRect.IsEmpty);
        Assert.IsTrue(layout.FeedButtonRect.IsEmpty);
        Assert.IsFalse(layout.PomodoroCardRect.IsEmpty);
        Assert.IsFalse(layout.TasksPanelRect.IsEmpty);
        Assert.AreEqual(3, layout.TaskRows.Count);
    }

    [TestMethod]
    public void HitTest_DispatchesCorrectActions()
    {
        SKRect bounds = new(0, 0, 1016, 592);
        var layout = CozyTodoLayout.Compute(bounds, isMinimalist: false, SampleTasks, scrollY: 0f);

        // Feed button hit
        SKPoint feedPoint = new(layout.FeedButtonRect.MidX, layout.FeedButtonRect.MidY);
        var feedAction = CozyTodoLayout.HitTest(layout, feedPoint);
        Assert.AreEqual(CozyTodoHitKind.FeedPet, feedAction.Kind);

        // Pomodoro button hit
        SKPoint pomoPoint = new(layout.PomodoroButtonRect.MidX, layout.PomodoroButtonRect.MidY);
        var pomoAction = CozyTodoLayout.HitTest(layout, pomoPoint);
        Assert.AreEqual(CozyTodoHitKind.ToggleFocus, pomoAction.Kind);

        // Task row hit
        var row = layout.TaskRows[1];
        SKPoint taskPoint = new(row.RowRect.MidX, row.RowRect.MidY);
        var taskAction = CozyTodoLayout.HitTest(layout, taskPoint);
        Assert.AreEqual(CozyTodoHitKind.ToggleTask, taskAction.Kind);
        Assert.AreEqual("t-2", taskAction.TaskId);

        // Miss hit
        SKPoint outsidePoint = new(-50, -50);
        var missAction = CozyTodoLayout.HitTest(layout, outsidePoint);
        Assert.AreEqual(CozyTodoHitKind.None, missAction.Kind);
    }

    [TestMethod]
    public void HitTest_WhenPointIsAboveTasksPanel_DoesNotHitScrolledRow()
    {
        SKRect bounds = new(0, 0, 1016, 592);
        // Scroll down by 200px so row 0 is positioned well above panel.Top
        var layout = CozyTodoLayout.Compute(bounds, isMinimalist: true, SampleTasks, scrollY: 200f);

        var scrolledRow = layout.TaskRows[0];
        Assert.IsTrue(scrolledRow.RowRect.Top < layout.TasksPanelRect.Top, "Row should be scrolled above tasks panel");

        // Tap at the coordinate of the scrolled-away row (inside the header)
        SKPoint pointInHeader = new(scrolledRow.RowRect.MidX, layout.TasksPanelRect.Top - 10f);
        var action = CozyTodoLayout.HitTest(layout, pointInHeader);

        Assert.AreEqual(CozyTodoHitKind.None, action.Kind, "Points outside TasksPanelRect must not trigger task rows");
    }

    [TestMethod]
    public void Compute_WithLargeTaskList_CalculatesAccurateMaxScrollAndRowPositions()
    {
        SKRect bounds = new(0, 0, 1016, 592);
        List<TodoItem> largeList = [];
        for (int i = 0; i < 150; i++)
        {
            largeList.Add(new TodoItem($"task-{i}", $"Task description #{i}", i % 2 == 0, "General"));
        }

        var layout = CozyTodoLayout.Compute(bounds, isMinimalist: false, largeList, scrollY: 0f);

        Assert.AreEqual(150, layout.TaskRows.Count);
        Assert.IsTrue(layout.MaxScrollOffset > 5000f, "150 tasks should yield significant max scroll extent");

        // Test with max scroll offset
        var scrolledLayout = CozyTodoLayout.Compute(bounds, isMinimalist: false, largeList, scrollY: layout.MaxScrollOffset);
        var lastRow = scrolledLayout.TaskRows[^1];
        Assert.IsTrue(lastRow.RowRect.Bottom <= layout.TasksPanelRect.Bottom + 50f, "Last task row should be positioned near bottom of panel at max scroll");
    }

    [TestMethod]
    public void Compute_WideLayout5x4_AvatarPillarIsLockedAndElementsAlignStrictlyWithinAvatarBounds()
    {
        SKRect bounds = new(0, 0, 1016, 592);
        var layout = CozyTodoLayout.Compute(bounds, isMinimalist: false, SampleTasks, scrollY: 0f);

        // Verify left column is locked to avatar boundary (~276px)
        Assert.IsTrue(layout.CompanionCardRect.Width <= 285f, "Left companion card should be docked tightly (~276px)");

        // Verify avatar box and all elements below it have identical horizontal span
        Assert.AreEqual(layout.PetSpriteRect.Left, layout.FeedButtonRect.Left, 1f, "Feed button must align with avatar box left");
        Assert.AreEqual(layout.PetSpriteRect.Right, layout.FeedButtonRect.Right, 1f, "Feed button must align with avatar box right");
        Assert.AreEqual(layout.PetSpriteRect.Left, layout.PomodoroCardRect.Left, 1f, "Pomodoro card must align with avatar box left");
        Assert.AreEqual(layout.PetSpriteRect.Right, layout.PomodoroCardRect.Right, 1f, "Pomodoro card must align with avatar box right");

        // Verify daily tasks panel expands across reclaimed horizontal space
        Assert.IsTrue(layout.TasksPanelRect.Width >= 680f, "Daily tasks panel should occupy ~690px (expanded from ~570px)");

        // Verify task row cards have enlarged height (>= 60px) for multi-line word wrapping
        Assert.IsTrue(layout.TaskRows[0].RowRect.Height >= 60f, "Task row card height should be at least 60px for readability and word wrapping");
    }
}
