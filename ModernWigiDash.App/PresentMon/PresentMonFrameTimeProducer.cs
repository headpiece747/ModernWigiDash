using System.Diagnostics;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.PresentMon;

/// <summary>
/// The App-side frame-time producer (ADR-0003). Polls the PresentMon Service
/// through the <see cref="IPresentMonNative"/> seam, resolves the tracking
/// target via <see cref="TrackedTargetResolver"/> (the foreground process
/// expanded to its descendant tree — multi-process apps present from child
/// GPU/renderer processes), and maps the dynamic-query + frame-query results
/// into a <see cref="FrameTimeSnapshotDto"/> that MainWindow's poll tick feeds
/// to <c>FrameTimeStore.UpdateFromDto</c>. PresentMon is a direct App↔service
/// connection — it is independent of any service routing.
/// </summary>
public sealed class PresentMonFrameTimeProducer : IDisposable
{
    /// <summary>Sparkline sample cap — aliases the single owner on FrameTimeStatistics.</summary>
    internal const int MaxSparklineSamples = FrameTimeStatistics.MaxSparklineSamples;

    /// <summary>
    /// Consecutive polls where at least one candidate tracked successfully but
    /// no present data arrived, before the capture is declared unhealthy. ~10s
    /// at the 1s poll cadence. Idle polls (no candidates) and track rejections
    /// never count toward this window.
    /// </summary>
    internal const int CaptureHealthGracePolls = 10;

    private readonly IPresentMonNative _native;
    private readonly TrackedTargetResolver _resolver;
    private readonly Func<int, string?> _processNameProvider;
    private readonly TimeProvider _timeProvider;
    private readonly List<double> _recentFrameTimes = [];
    private readonly TargetTrustPolicy _trust = new();

    private bool _sessionOpen;
    private int _emptyDataPolls;
    private FrameTimePollDiagnostics _lastDiagnostics = new(null, [], 0, 0, false, FrameTimeDiagOutcome.Unavailable);
    // The pids the service is currently tracking, kept in sync with the last
    // poll's candidate set (see ReconcileTracking).
    private readonly HashSet<int> _trackedPids = [];

    public PresentMonFrameTimeProducer(
        IPresentMonNative native,
        TrackedTargetResolver resolver,
        Func<int, string?>? processNameProvider = null,
        TimeProvider? timeProvider = null)
    {
        _native = native;
        _resolver = resolver;
        _processNameProvider = processNameProvider ?? DefaultProcessName;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Produces one frame-time snapshot. The DTO is never null: availability
    /// failures, an empty candidate set, and "no data yet" all yield a
    /// well-formed snapshot the widget can render (unavailable, or idle in the
    /// widget's no-process (zero) state with <see cref="FrameTimeSnapshotDto.ProcessId"/> = -1).
    /// Snapshot shaping concentrates in <see cref="FrameTimeSnapshotFactory"/>;
    /// this method owns the policy: which target, when to track, when the
    /// capture is dead.
    /// </summary>
    public FrameTimeSnapshotDto Poll()
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        if (!_native.IsAvailable)
        {
            return Result(FrameTimeSnapshotFactory.Unavailable(_native.UnavailableReason, now), FrameTimeDiagOutcome.Unavailable);
        }

        // Candidate policy (own-process exclusion included) belongs to the
        // resolver — it refuses the own pid at the root and never expands into
        // it. The producer only filters to positive, process-like ids.
        List<int> candidates = _resolver.ResolveCandidates()
            .Where(pid => pid > 0)
            .ToList();
        // Diagnostics: which window Windows reports as foreground and what the
        // resolver made of it — the evidence for target-transition bugs. The
        // policy state and outcome are refreshed by Result at each return.
        _lastDiagnostics = new FrameTimePollDiagnostics(
            _resolver.ForegroundWindowTitle(), candidates,
            _trust.LiveRootPid, _trust.ForeignStreak, _trust.CheckingAdopted,
            FrameTimeDiagOutcome.Idle);

        // Keep the service's tracked set == the current candidate set: the
        // dynamic query returns the tracked set's data, so a stale target
        // (e.g. a backgrounded fullscreen game that keeps presenting) would
        // otherwise keep reporting its hidden presents as every polled pid's
        // data. Stops happen before any tracking, so the set is reconciled
        // even on the idle and session paths.
        ReconcileTracking(candidates);

        if (candidates.Count == 0)
        {
            // Desktop/own-window idle must never count toward a dead capture —
            // the grace window belongs to a specific target, not to the gaps
            // between targets.
            _emptyDataPolls = 0;
            return Result(FrameTimeSnapshotFactory.Idle(now), FrameTimeDiagOutcome.NoCandidates);
        }

        // Target-trust decision (the policy owns the settling window, the
        // adoption, and the frozen-data guard — see TargetTrustPolicy).
        // TrackOnly carries the ordering contract: apply tracking, never poll.
        if (_trust.Decide(candidates) == TargetVerdict.TrackOnly)
        {
            TrackOnly(candidates);
            return Result(FrameTimeSnapshotFactory.Idle(now), FrameTimeDiagOutcome.Settling);
        }

        if (!_sessionOpen && !_native.OpenSession())
        {
            _emptyDataPolls = 0;
            return Result(FrameTimeSnapshotFactory.Unavailable(_native.UnavailableReason, now), FrameTimeDiagOutcome.Unavailable);
        }
        _sessionOpen = true;

        PollOutcome outcome = PollCandidates(candidates, _trackedPids);

        if (outcome.SessionLost)
        {
            _emptyDataPolls = 0;
            ResetSession();
            return Result(FrameTimeSnapshotFactory.Unavailable("PresentMon Service connection lost; reconnecting.", now), FrameTimeDiagOutcome.SessionLost);
        }

        if (outcome.Sample is not null)
        {
            if (outcome.Sample.DisplayedFps <= 0)
            {
                // The target presents but nothing of it reaches the display — a
                // backgrounded/minimized fullscreen game that keeps rendering.
                // PresentMon's DISPLAYED_FPS is the "is it actually on screen"
                // signal: the widget must read the idle zero state ("nothing to
                // display"), never the hidden present rate. The capture is
                // healthy — never a dead-capture count.
                _emptyDataPolls = 0;
                return Result(FrameTimeSnapshotFactory.Idle(now), FrameTimeDiagOutcome.NotDisplayed);
            }

            if (_trust.IsFrozenSample(outcome.Sample))
            {
                // Still the departed target's frozen data — the new target has
                // not presented yet. Keep the zero state; the guard clears on
                // the first differing sample.
                _emptyDataPolls = 0;
                return Result(FrameTimeSnapshotFactory.Idle(now), FrameTimeDiagOutcome.FrozenHold);
            }

            _emptyDataPolls = 0;
            _trust.NoteLive(outcome.ProcessId, outcome.Sample);
            AppendFrameTimes(_native.DrainFrameTimes(outcome.ProcessId));
            return Result(FrameTimeSnapshotFactory.Live(
                outcome.ProcessId,
                _processNameProvider(outcome.ProcessId) ?? string.Empty,
                outcome.Sample,
                _recentFrameTimes,
                now), FrameTimeDiagOutcome.Live);
        }

        if (!outcome.AnyTracked)
        {
            // Every candidate's track attempt was rejected — nothing is being
            // watched this poll. That is an idle-style outcome: a healthy
            // service that refuses tracking must never surface "capture
            // inactive". It neither counts toward a dead capture nor preserves
            // a partially spent grace window — the tracked-empty streak broke.
            _emptyDataPolls = 0;
            return Result(FrameTimeSnapshotFactory.Idle(now), FrameTimeDiagOutcome.Idle);
        }

        // At least one candidate tracked successfully but no present data
        // arrived this poll. The session is up and a target exists, so after a
        // grace period this means the service's ETW capture is not producing
        // present events — surface it instead of silently presenting
        // fabricated values as real FPS.
        if (++_emptyDataPolls >= CaptureHealthGracePolls)
        {
            return Result(FrameTimeSnapshotFactory.CaptureDead(now), FrameTimeDiagOutcome.CaptureDead);
        }

        return Result(FrameTimeSnapshotFactory.Idle(now), FrameTimeDiagOutcome.Idle);
    }

    /// <summary>Records the outcome on the diagnostics and returns the DTO —
    /// every return path funnels through here so the log state is always
    /// current (policy fields refreshed from the trust module).</summary>
    private FrameTimeSnapshotDto Result(FrameTimeSnapshotDto dto, FrameTimeDiagOutcome outcome)
    {
        _lastDiagnostics = new FrameTimePollDiagnostics(
            _lastDiagnostics.ForegroundTitle,
            _lastDiagnostics.Candidates,
            _trust.LiveRootPid,
            _trust.ForeignStreak,
            _trust.CheckingAdopted,
            outcome);
        return dto;
    }

    /// <summary>Per-poll target diagnostics (resolver report + trust-policy
    /// state + outcome), refreshed every poll; the telemetry tick composes
    /// the single log format from this record.</summary>
    internal FrameTimePollDiagnostics LastDiagnostics => _lastDiagnostics;

    public void Dispose()
    {
        _native.Dispose();
    }

    /// <summary>
    /// Stops tracking every pid that left the candidate set, then clears the
    /// bookkeeping (tracking is re-applied per poll — TrackProcess is
    /// idempotent at the native seam). A pid whose track attempt was rejected
    /// is never in the set, so it is never stopped.
    /// </summary>
    private void ReconcileTracking(List<int> candidates)
    {
        foreach (int tracked in _trackedPids.Where(tracked => !candidates.Contains(tracked)))
        {
            _native.StopTrackProcess(tracked);
        }
        _trackedPids.Clear();
    }

    /// <summary>Applies tracking to the candidates without polling any of them
    /// (the settling window: their samples are untrustworthy until adoption).</summary>
    private void TrackOnly(List<int> candidates)
    {
        // The native track call is a side effect, so it stays an explicit
        // statement instead of hiding inside a LINQ predicate.
        foreach (int pid in candidates)
        {
            if (!_native.TrackProcess(pid)) continue;
            _trackedPids.Add(pid);
        }
    }

    /// <summary>
    /// Applies tracking to every candidate and polls until the first live
    /// sample arrives. Carries the two outcomes that abort the loop early:
    /// a dead session (must reset) and a live sample (reports).
    /// </summary>
    private PollOutcome PollCandidates(List<int> candidates, HashSet<int> tracked)
    {
#pragma warning disable S125 // tracking-policy documentation, not commented-out code
        // Multi-process apps (Chrome/Edge/Electron) present from child GPU or
        // renderer processes, so poll the whole descendant tree. TrackProcess
        // is idempotent at the native seam (AlreadyTrackingProcess tolerated);
        // the first candidate that actually has data reports.
#pragma warning restore S125
        bool anyCandidateTracked = false;
        foreach (int pid in candidates)
        {
            if (!_native.TrackProcess(pid))
            {
                // A rejected track attempt is not empty data — skip the
                // candidate without counting it. The grace window resets only
                // when the whole poll ends with nothing tracked (below); a
                // mixed poll still counts once via its successfully tracked
                // candidate.
                continue;
            }
            tracked.Add(pid);
            anyCandidateTracked = true;

            var poll = _native.PollDynamic(pid);
            if (IsSessionLost(poll.Status))
            {
                return new PollOutcome(Sample: null, ProcessId: pid, AnyTracked: true, SessionLost: true);
            }
            if (poll.Sample is null)
            {
                continue;
            }

            return new PollOutcome(poll.Sample, pid, AnyTracked: true, SessionLost: false);
        }

        return new PollOutcome(Sample: null, ProcessId: -1, AnyTracked: anyCandidateTracked, SessionLost: false);
    }

    /// <summary>One candidate-polling pass: the sample that reported, or the
    /// flags the poll policy needs to decide the next outcome.</summary>
    private sealed record PollOutcome(
        PresentMonDynamicSample? Sample,
        int ProcessId,
        bool AnyTracked,
        bool SessionLost);

    /// <summary>Appends drained frame times and keeps the newest
    /// <see cref="MaxSparklineSamples"/> for the sparkline window.</summary>
    private void AppendFrameTimes(IReadOnlyList<double> frameTimesMs)
    {
        if (frameTimesMs.Count == 0)
        {
            return;
        }

        _recentFrameTimes.AddRange(frameTimesMs);
        if (_recentFrameTimes.Count > MaxSparklineSamples)
        {
            _recentFrameTimes.RemoveRange(0, _recentFrameTimes.Count - MaxSparklineSamples);
        }
    }

    /// <summary>Statuses that mean the session or pipe to the PresentMon Service
    /// is gone (service restarted, pipe broken) — the session must be torn down
    /// and re-established on the next poll tick. A benign "no data yet" poll is
    /// <see cref="PmStatus.Success"/> with a null sample and must NOT reset.</summary>
    private static bool IsSessionLost(PmStatus status) =>
        status is PmStatus.SessionNotOpen or PmStatus.PipeError or PmStatus.ServiceError;

    /// <summary>Drops the dead session so the next poll re-runs the
    /// open/track sequence. Tracking is re-applied because the fresh session
    /// has no tracking state — the bookkeeping clears with it.</summary>
    private void ResetSession()
    {
        _sessionOpen = false;
        _trackedPids.Clear();
        _native.CloseSession();
    }

    private static string? DefaultProcessName(int pid)
    {
        try
        {
            return Process.GetProcessById(pid).ProcessName;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
