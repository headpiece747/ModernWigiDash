using ModernWigiDash.App.PresentMon;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the frame-time poll's post-poll decision at its module interface:
/// given the polled outcome facts, which snapshot kind + diagnostic outcome +
/// streak update results. Driven without the PresentMon native seam — the
/// pure policy behind PresentMonFrameTimeProducer.Poll's tail.
/// </summary>
[TestClass]
public class FrameTimePollDecisionTests
{
    private static FrameTimePollDecision.PollFacts Facts(
        PresentMonDynamicSample? sample = null,
        bool frozenHold = false,
        bool anyTracked = true,
        int emptyDataStreak = 0) => new(sample, frozenHold, anyTracked, emptyDataStreak);

    [TestMethod]
    public void Decide_LiveSample_ReturnsLiveOutcomeAndNotesLive()
    {
        var sample = new PresentMonDynamicSample(Fps: 143.2, Low1PercentFps: 110.4, GpuBusyMs: 4.0, CpuFrameTimeMs: 4.05, DisplayedFps: 142.8, DroppedFrames: 2, GpuTimeMs: 6.1, PresentModeId: 4);

        var d = FrameTimePollDecision.Decide(Facts(sample: sample));

        Assert.AreEqual(FrameTimePollDecision.SnapshotKind.Live, d.Snapshot);
        Assert.AreEqual(FrameTimeDiagOutcome.Live, d.Outcome);
        Assert.IsTrue(d.ResetStreak, "a live sample resets the empty-data streak");
        Assert.IsFalse(d.IncrementStreak);
        Assert.IsTrue(d.NoteLive, "a live sample records the trusted target");
    }

    [TestMethod]
    public void Decide_SampleNotDisplayed_ReturnsIdleNotDisplayed()
    {
        // A backgrounded fullscreen game that keeps rendering: DISPLAYED_FPS is
        // the "is it actually on screen" signal. The widget reads the idle zero
        // state, never the hidden present rate; the capture stays healthy.
        var sample = new PresentMonDynamicSample(Fps: 143.2, Low1PercentFps: 110.4, GpuBusyMs: 4.0, CpuFrameTimeMs: 4.05, DisplayedFps: 0, DroppedFrames: 0, GpuTimeMs: 6.1, PresentModeId: 4);

        var d = FrameTimePollDecision.Decide(Facts(sample: sample));

        Assert.AreEqual(FrameTimePollDecision.SnapshotKind.Idle, d.Snapshot);
        Assert.AreEqual(FrameTimeDiagOutcome.NotDisplayed, d.Outcome);
        Assert.IsTrue(d.ResetStreak, "a not-displayed sample never counts toward a dead capture");
        Assert.IsFalse(d.NoteLive);
    }

    [TestMethod]
    public void Decide_FrozenHoldSample_ReturnsIdleFrozenHold()
    {
        // Still the departed target's frozen data — the adopted target has not
        // presented yet. Keep the zero state; the guard clears on the first
        // differing sample.
        var sample = new PresentMonDynamicSample(Fps: 143.2, Low1PercentFps: 110.4, GpuBusyMs: 4.0, CpuFrameTimeMs: 4.05, DisplayedFps: 142.8, DroppedFrames: 2, GpuTimeMs: 6.1, PresentModeId: 4);

        var d = FrameTimePollDecision.Decide(Facts(sample: sample, frozenHold: true));

        Assert.AreEqual(FrameTimePollDecision.SnapshotKind.Idle, d.Snapshot);
        Assert.AreEqual(FrameTimeDiagOutcome.FrozenHold, d.Outcome);
        Assert.IsTrue(d.ResetStreak);
        Assert.IsFalse(d.NoteLive, "a frozen sample must not record the trusted target");
    }

    [TestMethod]
    public void Decide_NoSampleNothingTracked_ReturnsIdleAndResetsStreak()
    {
        // Every candidate's track attempt was rejected — nothing is being
        // watched this poll. Idle-style: neither counts toward a dead capture
        // nor preserves a partially spent grace window.
        var d = FrameTimePollDecision.Decide(Facts(sample: null, anyTracked: false, emptyDataStreak: 5));

        Assert.AreEqual(FrameTimePollDecision.SnapshotKind.Idle, d.Snapshot);
        Assert.AreEqual(FrameTimeDiagOutcome.Idle, d.Outcome);
        Assert.IsTrue(d.ResetStreak, "the tracked-empty streak broke");
        Assert.IsFalse(d.IncrementStreak);
    }

    [TestMethod]
    public void Decide_NoSampleTrackedUnderGrace_ReturnsIdleAndIncrementsStreak()
    {
        // At least one candidate tracked but no present data arrived, and the
        // running total is still under the grace window: idle, increment.
        int grace = PresentMonFrameTimeProducer.CaptureHealthGracePolls;

        var d = FrameTimePollDecision.Decide(Facts(sample: null, anyTracked: true, emptyDataStreak: grace - 2));

        Assert.AreEqual(FrameTimePollDecision.SnapshotKind.Idle, d.Snapshot);
        Assert.AreEqual(FrameTimeDiagOutcome.Idle, d.Outcome);
        Assert.IsFalse(d.ResetStreak);
        Assert.IsTrue(d.IncrementStreak, "the streak advances toward the threshold");
    }

    [TestMethod]
    public void Decide_NoSampleTrackedAtGraceBoundary_ReturnsCaptureDead()
    {
        // The pre-increment count is one below the grace window: this poll adds
        // one, reaching the threshold exactly, so the capture is declared dead.
        int grace = PresentMonFrameTimeProducer.CaptureHealthGracePolls;

        var d = FrameTimePollDecision.Decide(Facts(sample: null, anyTracked: true, emptyDataStreak: grace - 1));

        Assert.AreEqual(FrameTimePollDecision.SnapshotKind.CaptureDead, d.Snapshot);
        Assert.AreEqual(FrameTimeDiagOutcome.CaptureDead, d.Outcome);
        Assert.IsFalse(d.ResetStreak);
        Assert.IsTrue(d.IncrementStreak, "the final poll still increments before surfacing");
    }

    [TestMethod]
    public void Decide_NoSampleTrackedPastGrace_ReturnsCaptureDead()
    {
        // Past the grace window the capture-dead verdict holds.
        int grace = PresentMonFrameTimeProducer.CaptureHealthGracePolls;

        var d = FrameTimePollDecision.Decide(Facts(sample: null, anyTracked: true, emptyDataStreak: grace + 3));

        Assert.AreEqual(FrameTimePollDecision.SnapshotKind.CaptureDead, d.Snapshot);
        Assert.AreEqual(FrameTimeDiagOutcome.CaptureDead, d.Outcome);
    }
}
