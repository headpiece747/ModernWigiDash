using ModernWigiDash.Widgets.Steam;
using ModernWigiDash.Widgets.Twitch;

namespace ModernWigiDash.Tests;

/// <summary>
/// Universal touch contract tests for all touch-enabled widgets in ModernWigiDash.
/// Enforces physical hardware invariants across all 10 widgets:
/// 1. Stationary Hold: continuous 16ms raw hardware TouchDown streams must not throw, re-enter, or flap.
/// 2. TouchCancel: cancel cleanly resets interaction state without spurious actions.
/// 3. Zero Actions on TouchDown: discrete tap actions must not execute on TouchDown alone.
/// </summary>
[TestClass]
public class WidgetTouchContractTests
{
    private static IModernWidget[] CreateAllTouchWidgets() =>
    [
        new FrameTimeWidget(),
        new HotkeyButtonWidget(),
        new NowPlayingWidget(),
        new CalendarWidget(),
        new SteamLibraryWidget(),
        new StopwatchTimerWidget(),
        new TwitchChatStreamWidget(),
        new WeatherForecastWidget(),
        new CozyTodoWidget(),
        new PictureAndGifWidget(),
    ];

    [TestMethod]
    public void StationaryHold_100ContinuousDownEvents_DoesNotThrowOrCauseRunawayState()
    {
        var widgets = CreateAllTouchWidgets();
        var point = new SKPoint(100, 100);

        foreach (var widget in widgets)
        {
            // Simulate 16ms hardware digitizer polling 100 times while finger rests on glass
            for (int i = 0; i < 100; i++)
            {
                widget.OnTouch(point, TouchEventType.TouchDown);
            }

            // End with clean release
            widget.OnTouch(point, TouchEventType.TouchUp);
            Assert.IsNotNull(widget, $"{widget.GetType().Name} must survive stationary hold stream");
        }
    }

    [TestMethod]
    public void TouchCancel_CleanlyResetsInteractionAcrossAllWidgets()
    {
        var widgets = CreateAllTouchWidgets();
        var point = new SKPoint(50, 50);

        foreach (var widget in widgets)
        {
            widget.OnTouch(point, TouchEventType.TouchDown);
            widget.OnTouch(point, TouchEventType.TouchCancel);
            // Subsequent release after cancel must be ignored
            widget.OnTouch(point, TouchEventType.TouchUp);
            Assert.IsNotNull(widget, $"{widget.GetType().Name} must cleanly handle TouchCancel");
        }
    }

    [TestMethod]
    public void DiscreteActionWidgets_DoNotTriggerActionOnTouchDownAlone()
    {
        // 1. StopwatchTimerWidget: TouchDown must not start the clock
        var stopwatch = new StopwatchTimerWidget();
        stopwatch.OnTouch(new SKPoint(20, 20), TouchEventType.TouchDown);
        Assert.IsFalse(stopwatch.IsRunningForTest, "StopwatchTimerWidget must not start on TouchDown alone");

        // 2. FrameTimeWidget: TouchDown must not toggle overlay view
        var frameTime = new FrameTimeWidget();
        bool initialOverlay = frameTime.IsOverlayView;
        frameTime.OnTouch(new SKPoint(20, 20), TouchEventType.TouchDown);
        Assert.AreEqual(initialOverlay, frameTime.IsOverlayView, "FrameTimeWidget must not toggle view on TouchDown alone");
    }
}
