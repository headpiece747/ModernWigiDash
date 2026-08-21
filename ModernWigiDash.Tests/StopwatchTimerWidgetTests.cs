
namespace ModernWigiDash.Tests;

/// <summary>
/// The stopwatch's timing math, driven by an injectable clock — the widget
/// was previously untestable (TimeProvider.System hardcoded).
/// </summary>
[TestClass]
public class StopwatchTimerWidgetTests
{
    private static SKCanvas CreateCanvas()
        => SKSurface.Create(new SKImageInfo(203, 148)).Canvas;

    [TestMethod]
    public void Start_ThenPause_AccumulatesElapsed()
    {
        var clock = new FakeTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };
        widget.InitializeAsync(new TestContext()).AsTask().GetAwaiter().GetResult();

        widget.OnTouch(default, TouchEventType.TouchDown); // start
        clock.Advance(TimeSpan.FromSeconds(5));
        widget.OnTouch(default, TouchEventType.TouchDown); // pause

        Assert.AreEqual(TimeSpan.FromSeconds(5), widget.ElapsedForTest);
    }

    [TestMethod]
    public void Running_Elapsed_TracksClock()
    {
        var clock = new FakeTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };

        widget.OnTouch(default, TouchEventType.TouchDown); // start
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.AreEqual(TimeSpan.FromSeconds(3), widget.ElapsedForTest, "A running stopwatch must track the clock");
    }

    [TestMethod]
    public void Paused_Elapsed_IsFrozen()
    {
        var clock = new FakeTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };

        widget.OnTouch(default, TouchEventType.TouchDown); // start
        clock.Advance(TimeSpan.FromSeconds(2));
        widget.OnTouch(default, TouchEventType.TouchDown); // pause
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.AreEqual(TimeSpan.FromSeconds(2), widget.ElapsedForTest, "A paused stopwatch must not advance with the clock");
    }

    [TestMethod]
    public void Render_WithRunningStopwatch_DrawsWithoutException()
    {
        var clock = new FakeTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };
        widget.OnTouch(default, TouchEventType.TouchDown);
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
}
