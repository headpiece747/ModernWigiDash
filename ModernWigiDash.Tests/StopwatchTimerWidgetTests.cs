using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// The stopwatch's timing math, driven by an injectable clock — the widget
/// was previously untestable (TimeProvider.System hardcoded).
/// </summary>
[TestClass]
public class StopwatchTimerWidgetTests
{
    private sealed class MutableTimeProvider : TimeProvider
    {
        public DateTimeOffset UtcNowValue { get; set; } = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNowValue;
    }

    private static SKCanvas CreateCanvas()
        => SKSurface.Create(new SKImageInfo(203, 148)).Canvas;

    [TestMethod]
    public void Start_ThenPause_AccumulatesElapsed()
    {
        var clock = new MutableTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };
        widget.InitializeAsync(new StubContext()).AsTask().GetAwaiter().GetResult();

        widget.OnTouch(default, TouchEventType.TouchDown); // start
        clock.UtcNowValue += TimeSpan.FromSeconds(5);
        widget.OnTouch(default, TouchEventType.TouchDown); // pause

        Assert.AreEqual(TimeSpan.FromSeconds(5), widget.ElapsedForTest);
    }

    [TestMethod]
    public void Running_Elapsed_TracksClock()
    {
        var clock = new MutableTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };

        widget.OnTouch(default, TouchEventType.TouchDown); // start
        clock.UtcNowValue += TimeSpan.FromSeconds(3);

        Assert.AreEqual(TimeSpan.FromSeconds(3), widget.ElapsedForTest, "A running stopwatch must track the clock");
    }

    [TestMethod]
    public void Paused_Elapsed_IsFrozen()
    {
        var clock = new MutableTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };

        widget.OnTouch(default, TouchEventType.TouchDown); // start
        clock.UtcNowValue += TimeSpan.FromSeconds(2);
        widget.OnTouch(default, TouchEventType.TouchDown); // pause
        clock.UtcNowValue += TimeSpan.FromSeconds(10);

        Assert.AreEqual(TimeSpan.FromSeconds(2), widget.ElapsedForTest, "A paused stopwatch must not advance with the clock");
    }

    [TestMethod]
    public void Render_WithRunningStopwatch_DrawsWithoutException()
    {
        var clock = new MutableTimeProvider();
        var widget = new StopwatchTimerWidget { Clock = clock };
        widget.OnTouch(default, TouchEventType.TouchDown);
        clock.UtcNowValue += TimeSpan.FromSeconds(1);

        widget.Render(CreateCanvas(), new SKRect(0, 0, 203, 148));

        Assert.IsTrue(widget.ElapsedForTest >= TimeSpan.FromSeconds(1), "A running stopwatch must have accrued the elapsed time");
    }

    private sealed class StubContext : IModernWigiDashContext
    {
        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void RequestRender() { }
        public void RequestInspectorRefresh() { }
        public void ShowDeviceAuthorization(string serviceName, Uri verificationUri, string userCode, DateTimeOffset expiresAt) { }
        public void CloseDeviceAuthorization() { }
    }
}
