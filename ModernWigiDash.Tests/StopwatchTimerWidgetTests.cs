namespace ModernWigiDash.Tests;

[TestClass]
public class StopwatchTimerWidgetTests
{
    private static SKCanvas CreateCanvas()
        => SKSurface.Create(new SKImageInfo(203, 148)).Canvas;

    private static void Tap(StopwatchTimerWidget widget)
    {
        widget.OnTouch(default, TouchEventType.TouchDown);
        widget.OnTouch(default, TouchEventType.TouchUp);
    }

    [TestMethod]
    public void Start_ThenPause_AccumulatesElapsed()
    {
        var clock = new FakeTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };
        widget.InitializeAsync(new TestContext()).AsTask().GetAwaiter().GetResult();

        Tap(widget); // start
        clock.Advance(TimeSpan.FromSeconds(5));
        Tap(widget); // pause

        Assert.AreEqual(TimeSpan.FromSeconds(5), widget.ElapsedForTest);
    }

    [TestMethod]
    public void Running_Elapsed_TracksClock()
    {
        var clock = new FakeTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };

        Tap(widget); // start
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.AreEqual(TimeSpan.FromSeconds(3), widget.ElapsedForTest, "A running stopwatch must track the clock");
    }

    [TestMethod]
    public void Paused_Elapsed_IsFrozen()
    {
        var clock = new FakeTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };

        Tap(widget); // start
        clock.Advance(TimeSpan.FromSeconds(2));
        Tap(widget); // pause
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.AreEqual(TimeSpan.FromSeconds(2), widget.ElapsedForTest, "A paused stopwatch must not advance with the clock");
    }

    [TestMethod]
    public void Render_WithRunningStopwatch_DrawsWithoutException()
    {
        var clock = new FakeTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };
        Tap(widget);
        clock.Advance(TimeSpan.FromSeconds(1));

        widget.Render(CreateCanvas(), new SKRect(0, 0, 203, 148));

        Assert.IsTrue(widget.ElapsedForTest >= TimeSpan.FromSeconds(1), "A running stopwatch must have accrued the elapsed time");
    }

    [TestMethod]
    public void Render_CustomColors_ExecutesWithoutExceptions()
    {
        var widget = new StopwatchTimerWidget { TextColorHex = "#C6E0FF" };

        widget.Render(CreateCanvas(), new SKRect(0, 0, 203, 148));

        Assert.AreEqual(TimeSpan.Zero, widget.ElapsedForTest, "A never-started stopwatch renders its idle zero state");
    }

    [TestMethod]
    public void TouchDown_Alone_DoesNotStartStopwatch()
    {
        var widget = new StopwatchTimerWidget();
        widget.OnTouch(default, TouchEventType.TouchDown);

        Assert.IsFalse(widget.IsRunningForTest, "TouchDown alone must not trigger actions (actions occur on TouchUp)");
    }

    [TestMethod]
    public void StationaryHold_RepeatedTouchDown_DoesNotToggle()
    {
        var widget = new StopwatchTimerWidget();

        // 16ms hardware loop reports Down repeatedly while finger is held
        widget.OnTouch(default, TouchEventType.TouchDown);
        widget.OnTouch(default, TouchEventType.TouchDown);
        widget.OnTouch(default, TouchEventType.TouchDown);

        Assert.IsFalse(widget.IsRunningForTest, "Stationary hold with repeated Down events must not toggle");

        widget.OnTouch(default, TouchEventType.TouchUp);
        Assert.IsTrue(widget.IsRunningForTest, "Release must start the stopwatch exactly once");
    }

    [TestMethod]
    public void TouchCancel_AbortsPendingTap()
    {
        var widget = new StopwatchTimerWidget();

        widget.OnTouch(default, TouchEventType.TouchDown);
        widget.OnTouch(default, TouchEventType.TouchCancel);
        widget.OnTouch(default, TouchEventType.TouchUp);

        Assert.IsFalse(widget.IsRunningForTest, "TouchCancel must abort the pending tap");
    }
}
