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
/// connection — it is independent of the WCF service routing.
/// </summary>
public sealed class PresentMonFrameTimeProducer : IDisposable
{
    /// <summary>Sparkline sample cap — mirrors the widget's test window of 240 samples.</summary>
    internal const int MaxSparklineSamples = 240;

    /// <summary>
    /// Consecutive polls with a candidate set but no present data anywhere
    /// before the capture is declared unhealthy. ~10s at the 1s poll cadence.
    /// </summary>
    private const int CaptureHealthGracePolls = 10;

    private readonly IPresentMonNative _native;
    private readonly TrackedTargetResolver _resolver;
    private readonly Func<int, string?> _processNameProvider;
    private readonly TimeProvider _timeProvider;
    private readonly List<double> _recentFrameTimes = [];

    private bool _sessionOpen;
    private int _trackedPid = -1;
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
    /// well-formed snapshot the widget can render (unavailable, or idle in
    /// monitor-refresh mode with <see cref="FrameTimeSnapshotDto.ProcessId"/> = -1).
    /// </summary>
    public FrameTimeSnapshotDto Poll()
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        if (!_native.IsAvailable)
        {
            return Unavailable(_native.UnavailableReason, now);
        }

        // Skip our own process — its render loop presents to the WigiDash
        // window and would dominate the "foreground app" FPS readout. The
        // resolver already excludes it; filtering here keeps the producer
        // robust to whatever the seam returns.
        List<int> candidates = _resolver.ResolveCandidates()
            .Where(pid => pid > 0 && !TrackedTargetResolver.IsOwnProcess(pid))
            .ToList();
        if (candidates.Count == 0)
        {
            return Idle(now);
        }

        if (!_sessionOpen && !_native.OpenSession())
        {
            _emptyDataPolls = 0;
            return Unavailable(_native.UnavailableReason, now);
        }
        _sessionOpen = true;

        // Multi-process apps (Chrome/Edge/Electron) present from child GPU or
        // renderer processes, so poll the whole descendant tree. TrackProcess
        // is idempotent at the native seam (AlreadyTrackingProcess tolerated);
        // the first candidate that actually has data reports.
        foreach (int pid in candidates)
        {
            if (!_native.TrackProcess(pid))
            {
                continue;
            }

            var poll = _native.PollDynamic(pid);
            if (IsSessionLost(poll.Status))
            {
                _emptyDataPolls = 0;
                ResetSession();
                return Unavailable("PresentMon Service connection lost; reconnecting.", now);
            }
            if (poll.Sample is null)
            {
                continue;
            }

            _emptyDataPolls = 0;
            _trackedPid = pid;
            AppendFrameTimes(_native.DrainFrameTimes(pid));

            return new FrameTimeSnapshotDto
            {
                IsAvailable = true,
                LastUpdate = now,
                ProcessId = pid,
                ProcessName = _processNameProvider(pid) ?? string.Empty,
                Fps = poll.Sample.Fps,
                FrameTimeMs = poll.Sample.Fps > 0 ? 1000.0 / poll.Sample.Fps : 0,
                Low1PercentFps = poll.Sample.Low1PercentFps,
                Low01PercentFps = FrameTimeStatistics.Low01PercentFps(_recentFrameTimes),
                GpuBusyMs = poll.Sample.GpuBusyMs,
                CpuFrameTimeMs = poll.Sample.CpuFrameTimeMs,
                RecentFrameTimesMs = new List<double>(_recentFrameTimes),
            };
        }

        // No candidate produced data this poll. The session is up and a target
        // exists, so after a grace period this means the service's ETW capture
        // is not producing present events — surface it instead of silently
        // presenting monitor-refresh mode as real FPS.
        if (++_emptyDataPolls >= CaptureHealthGracePolls)
        {
            return CaptureDead(now);
        }

        return Idle(now);
    }

    public void Dispose()
    {
        _native.Dispose();
    }

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
        _trackedPid = -1;
        _native.CloseSession();
    }

    private static FrameTimeSnapshotDto Unavailable(string? reason, DateTime now) => new()
    {
        IsAvailable = false,
        ErrorMessage = reason ?? string.Empty,
        LastUpdate = now,
    };

    private static FrameTimeSnapshotDto Idle(DateTime now) => new()
    {
        IsAvailable = true,
        ProcessId = -1,
        LastUpdate = now,
    };

    /// <summary>
    /// Session is up, a target exists, but no present data has arrived for the
    /// whole grace window — the service's ETW capture is not producing events.
    /// The DTO stays "available" (the service is reachable) but flags the
    /// capture unhealthy so the widget can say so instead of showing
    /// monitor-refresh mode as real FPS.
    /// </summary>
    private static FrameTimeSnapshotDto CaptureDead(DateTime now) => new()
    {
        IsAvailable = true,
        CaptureHealthy = false,
        ErrorMessage = "PresentMon capture is not producing present data (service ETW capture inactive).",
        ProcessId = -1,
        LastUpdate = now,
    };

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
