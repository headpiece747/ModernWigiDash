namespace ModernWigiDash.Tests;

using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Todo;



/// <summary>
/// The Cozy To-Do gesture state machine's pins: the tap-vs-drag distinction
/// (the <c>TapDragTolerance</c> rule), the scroll update on a drag, and the
/// tap dispatch to the widget callbacks.
/// </summary>
[TestClass]
public class CozyTodoGestureStateTests
{
    private static CozyTodoGeometry MakeGeometry(IReadOnlyList<TodoItem> tasks)
        => CozyTodoLayout.Compute(new SKRect(0, 0, 1016f, 592f), isMinimalist: true, tasks, scrollY: 0f);

    [TestMethod]
    public void Feed_TapOnTaskRow_DispatchesToggle()
    {
        var tasks = new List<TodoItem> { new("t-1", "Task One", false) };
        var geometry = MakeGeometry(tasks);
        var gesture = new CozyTodoGestureState();
        gesture.SetFrameGeometry(geometry);

        string? toggledId = null;
        gesture.OnTaskToggled = id => toggledId = id;

        // Tap the first task row's center.
        var row = geometry.TaskRows[0];
        var tapPoint = new SKPoint((row.RowRect.Left + row.RowRect.Right) / 2f, (row.RowRect.Top + row.RowRect.Bottom) / 2f);
        gesture.Feed(tapPoint, TouchEventType.TouchDown);
        gesture.Feed(tapPoint, TouchEventType.TouchUp);

        Assert.AreEqual("t-1", toggledId, "a tap on a task row dispatches the toggle callback");
    }

    [TestMethod]
    public void Feed_DragBeyondTolerance_NoTapAndScrollUpdates()
    {
        // Enough tasks that the content exceeds the viewable panel height,
        // so the scroll offset is non-zero and the drag can move it.
        var tasks = new List<TodoItem>();
        for (int i = 0; i < 12; i++)
            tasks.Add(new($"t-{i}", $"Task {i}", false));
        var geometry = MakeGeometry(tasks);
        var gesture = new CozyTodoGestureState();
        gesture.SetFrameGeometry(geometry);

        string? toggledId = null;
        gesture.OnTaskToggled = id => toggledId = id;

        // Start a touch on the first row, drag up beyond the tolerance
        // (the scroll formula is PressPoint.Y - localPoint.Y, so an upward
        // drag produces a positive scroll delta).
        var row = geometry.TaskRows[0];
        var startPoint = new SKPoint((row.RowRect.Left + row.RowRect.Right) / 2f, (row.RowRect.Top + row.RowRect.Bottom) / 2f);
        float dragDistance = CozyTodoGestureState.TapDragTolerance + 10f;
        var endPoint = new SKPoint(startPoint.X, startPoint.Y - dragDistance);

        gesture.Feed(startPoint, TouchEventType.TouchDown);
        gesture.Feed(endPoint, TouchEventType.TouchMove);
        gesture.Feed(endPoint, TouchEventType.TouchUp);

        Assert.IsNull(toggledId, "a drag beyond the tolerance does not dispatch a tap");
        Assert.IsTrue(gesture.ScrollY > 0f, "a drag updates the scroll position");
    }

    [TestMethod]
    public void Feed_MoveWithinTolerance_StillDispatchesTap()
    {
        var tasks = new List<TodoItem> { new("t-1", "Task One", false) };
        var geometry = MakeGeometry(tasks);
        var gesture = new CozyTodoGestureState();
        gesture.SetFrameGeometry(geometry);

        string? toggledId = null;
        gesture.OnTaskToggled = id => toggledId = id;

        // Start a touch, move a small amount (within tolerance), release.
        var row = geometry.TaskRows[0];
        var startPoint = new SKPoint((row.RowRect.Left + row.RowRect.Right) / 2f, (row.RowRect.Top + row.RowRect.Bottom) / 2f);
        var endPoint = new SKPoint(startPoint.X, startPoint.Y + 5f); // 5 < 15 tolerance

        gesture.Feed(startPoint, TouchEventType.TouchDown);
        gesture.Feed(endPoint, TouchEventType.TouchMove);
        gesture.Feed(endPoint, TouchEventType.TouchUp);

        Assert.AreEqual("t-1", toggledId, "a move within the tolerance still dispatches a tap");
    }

    [TestMethod]
    public void Feed_TapOnFeedButton_DispatchesFeedCallback()
    {
        var tasks = new List<TodoItem> { new("t-1", "Task One", false) };
        var geometry = MakeGeometry(tasks);
        var gesture = new CozyTodoGestureState();
        gesture.SetFrameGeometry(geometry);

        bool feedTapped = false;
        gesture.OnFeedTapped = () => feedTapped = true;

        // In minimalist mode the feed button is empty (SKRect.Empty), so use
        // the companion layout for a geometry with a feed button.
        var companionGeometry = CozyTodoLayout.Compute(new SKRect(0, 0, 508f, 592f), isMinimalist: false, tasks, scrollY: 0f);
        gesture.SetFrameGeometry(companionGeometry);

        var feedPoint = new SKPoint((companionGeometry.FeedButtonRect.Left + companionGeometry.FeedButtonRect.Right) / 2f, (companionGeometry.FeedButtonRect.Top + companionGeometry.FeedButtonRect.Bottom) / 2f);
        gesture.Feed(feedPoint, TouchEventType.TouchDown);
        gesture.Feed(feedPoint, TouchEventType.TouchUp);

        Assert.IsTrue(feedTapped, "a tap on the feed button dispatches the feed callback");
    }
}
