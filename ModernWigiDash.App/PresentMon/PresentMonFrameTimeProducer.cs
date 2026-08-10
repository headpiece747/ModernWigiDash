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

    private bool _sessionOpen;
    private int _emptyDataPolls;

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
            return FrameTimeSnapshotFactory.Unavailable(_native.UnavailableReason, now);
        }

        // Candidate policy (own-process exclusion included) belongs to the
        // resolver — it refuses the own pid at the root and never expands into
        // it. The producer only filters to positive, process-like ids.
        List<int> candidates = _resolver.ResolveCandidates()
            .Where(pid => pid > 0)
            .ToList();
        if (candidates.Count == 0)
        {
            // Desktop/own-window idle must never count toward a dead capture —
            // the grace window belongs to a specific target, not to the gaps
            // between targets.
            _emptyDataPolls = 0;
            return FrameTimeSnapshotFactory.Idle(now);
        }

        if (!_sessionOpen && !_native.OpenSession())
        {
            _emptyDataPolls = 0;
            return FrameTimeSnapshotFactory.Unavailable(_native.UnavailableReason, now);
        }
        _sessionOpen = true;

        PollOutcome outcome = PollCandidates(candidates);

        if (outcome.SessionLost)
        {
            _emptyDataPolls = 0;
            ResetSession();
            return FrameTimeSnapshotFactory.Unavailable("PresentMon Service connection lost; reconnecting.", now);
        }

        if (outcome.Sample is not null)
        {
            _emptyDataPolls = 0;
            AppendFrameTimes(_native.DrainFrameTimes(outcome.ProcessId));
            return FrameTimeSnapshotFactory.Live(
                outcome.ProcessId,
                _processNameProvider(outcome.ProcessId) ?? string.Empty,
                outcome.Sample,
                _recentFrameTimes,
                now);
        }

        if (!outcome.AnyTracked)
        {
            // Every candidate's track attempt was rejected — nothing is being
            // watched this poll. That is an idle-style outcome: a healthy
            // service that refuses tracking must never surface "capture
            // inactive". It neither counts toward a dead capture nor preserves
            // a partially spent grace window — the tracked-empty streak broke.
            _emptyDataPolls = 0;
            return FrameTimeSnapshotFactory.Idle(now);
        }

        // At least one candidate tracked successfully but no present data
        // arrived this poll. The session is up and a target exists, so after a
        // grace period this means the service's ETW capture is not producing
        // present events — surface it instead of silently presenting
        // fabricated values as real FPS.
        if (++_emptyDataPolls >= CaptureHealthGracePolls)
        {
            return FrameTimeSnapshotFactory.CaptureDead(now);
        }

        return FrameTimeSnapshotFactory.Idle(now);
    }

    public void Dispose()
    {
        _native.Dispose();
    }

    /// <summary>
    /// Applies tracking to every candidate and polls until the first live
    /// sample arrives. Carries the two outcomes that abort the loop early:
    /// a dead session (must reset) and a live sample (reports).
    /// </summary>
    private PollOutcome PollCandidates(List<int> candidates)
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
    /// has no tracking state.</summary>
    private void ResetSession()
    {
        _sessionOpen = false;
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
