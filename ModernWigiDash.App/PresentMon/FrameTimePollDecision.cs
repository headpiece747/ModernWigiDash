namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// The frame-time poll's post-poll decision: given the polled outcome (a live
/// sample or none, whether it is actually displayed, the trust policy's
/// frozen-data verdict, whether anything was tracked, and the running
/// empty-data streak), decide which snapshot the widget renders and which
/// diagnostic outcome the telemetry tick logs. The producer owns the
/// sequencing side effects (availability, candidate resolution, tracking
/// reconciliation, the settling window, the session open/close, the frame-time
/// buffer) and routes this final decision through the module, so the
/// sample-vs-no-sample policy is assertable at its interface without the
/// PresentMon native seam.
/// </summary>
internal static class FrameTimePollDecision
{
    /// <summary>The snapshot kind the decision selects — the producer builds
    /// the actual <see cref="FrameTimeSnapshotDto"/> through
    /// <see cref="FrameTimeSnapshotFactory"/> using the matching arm.</summary>
    internal enum SnapshotKind
    {
        /// <summary>No target / not-displayed / frozen-hold / idle.</summary>
        Idle,
        /// <summary>A live present sample for the tracked process.</summary>
        Live,
        /// <summary>The capture is dead after the grace window.</summary>
        CaptureDead,
    }

    /// <summary>One poll's post-poll facts — everything the decision reads.
    /// The producer assembles this from the polled outcome + trust policy + its
    /// own empty-data bookkeeping, then hands it in. <see cref="FrozenHold"/> is
    /// the trust policy's after-poll guard verdict (the producer applies it
    /// before handing the facts in, because it mutates the policy).</summary>
    internal sealed record PollFacts(
        PresentMonDynamicSample? Sample,
        bool FrozenHold,
        bool AnyTracked,
        int EmptyDataStreak);

    /// <summary>The decision: which snapshot kind + diagnostic outcome, and
    /// how the empty-data streak updates (reset to zero, or incremented toward
    /// the capture-dead threshold).</summary>
    internal sealed record Decision(
        SnapshotKind Snapshot,
        FrameTimeDiagOutcome Outcome,
        bool ResetStreak,
        bool IncrementStreak,
        bool NoteLive);

    /// <summary>
    /// Decides one poll's post-poll outcome from its facts. The branch order
    /// mirrors the producer's former inline tail: the sample-present arms
    /// (not-displayed, frozen-hold, live), then the no-sample arms (untracked
    /// idle vs. the grace-window capture-dead).
    /// </summary>
    public static Decision Decide(PollFacts f)
    {
        if (f.Sample is not null)
        {
            if (f.Sample.DisplayedFps <= 0)
            {
                // The target presents but nothing of it reaches the display — a
                // backgrounded/minimized fullscreen game that keeps rendering.
                // PresentMon's DISPLAYED_FPS is the "is it actually on screen"
                // signal: the widget must read the idle zero state, never the
                // hidden present rate. The capture is healthy — never a
                // dead-capture count.
                return new Decision(SnapshotKind.Idle, FrameTimeDiagOutcome.NotDisplayed, true, false, false);
            }

            if (f.FrozenHold)
            {
                // Still the departed target's frozen data — the new target has
                // not presented yet. Keep the zero state; the guard clears on
                // the first differing sample.
                return new Decision(SnapshotKind.Idle, FrameTimeDiagOutcome.FrozenHold, true, false, false);
            }

            return new Decision(SnapshotKind.Live, FrameTimeDiagOutcome.Live, true, false, true);
        }

        if (!f.AnyTracked)
        {
            // Every candidate's track attempt was rejected — nothing is being
            // watched this poll. That is an idle-style outcome: a healthy
            // service that refuses tracking must never surface "capture
            // inactive". It neither counts toward a dead capture nor preserves
            // a partially spent grace window — the tracked-empty streak broke.
            return new Decision(SnapshotKind.Idle, FrameTimeDiagOutcome.Idle, true, false, false);
        }

        // At least one candidate tracked successfully but no present data
        // arrived this poll. After the grace window this means the service's
        // ETW capture is not producing present events — surface it instead of
        // silently presenting fabricated values as real FPS. EmptyDataStreak is
        // the pre-increment count; this poll adds one, so the threshold is
        // reached when the running total hits the grace window.
        if (f.EmptyDataStreak + 1 >= PresentMonFrameTimeProducer.CaptureHealthGracePolls)
        {
            return new Decision(SnapshotKind.CaptureDead, FrameTimeDiagOutcome.CaptureDead, false, true, false);
        }

        return new Decision(SnapshotKind.Idle, FrameTimeDiagOutcome.Idle, false, true, false);
    }
}
