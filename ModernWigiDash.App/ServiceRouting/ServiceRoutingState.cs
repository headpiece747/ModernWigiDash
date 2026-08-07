using System;

namespace ModernWigiDash.App.ServiceRouting;

/// <summary>
/// Owns the App↔service routing truth: whether the service is active, the
/// consecutive-failure counting that flips it, and the throttled re-detect
/// trigger. Poll loops read <see cref="IsServiceActive"/> as their readiness
/// guard and report failures here — a service that dies after a successful
/// connect stops the loops within a couple of failures instead of hammering a
/// faulted channel, and re-detection is throttled so it cannot storm.
/// </summary>
public sealed class ServiceRoutingState
{
    private readonly int _failureThreshold;
    private readonly TimeSpan _retryInterval;
    private readonly Action _onReconnect;
    private readonly Action<string> _log;
    private readonly TimeProvider _timeProvider;

    private int _consecutiveFailures;
    private DateTimeOffset _lastRetry = DateTimeOffset.MinValue;

    /// <param name="failureThreshold">Consecutive poll failures that flip the
    /// state to inactive (default 2 — the 16ms touch loop trips it in ~32ms).</param>
    /// <param name="retryInterval">Minimum interval between re-detect triggers.</param>
    /// <param name="onReconnect">Invoked (throttled) when the state flips
    /// inactive after failures — re-runs service detection.</param>
    public ServiceRoutingState(
        int failureThreshold = 2,
        TimeSpan? retryInterval = null,
        Action? onReconnect = null,
        Action<string>? log = null,
        TimeProvider? timeProvider = null)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _retryInterval = retryInterval ?? TimeSpan.FromSeconds(10);
        _onReconnect = onReconnect ?? (() => { });
        _log = log ?? (_ => { });
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsServiceActive { get; private set; }

    /// <summary>Called when detection succeeds and the client is bound.</summary>
    public void MarkActive()
    {
        IsServiceActive = true;
        _consecutiveFailures = 0;
    }

    /// <summary>Called when detection fails or the client is torn down.</summary>
    public void MarkInactive()
    {
        IsServiceActive = false;
        _consecutiveFailures = 0;
    }

    /// <summary>
    /// Reports one failed poll tick. After <see cref="_failureThreshold"/>
    /// consecutive failures the service is marked inactive and a throttled
    /// re-detect is triggered, so poll loops pause instead of churning.
    /// </summary>
    public void ReportFailure()
    {
        _consecutiveFailures++;
        if (!IsServiceActive) return;

        if (_consecutiveFailures >= _failureThreshold)
        {
            _log($"[WCF] {_consecutiveFailures} consecutive poll failures — marking service inactive, scheduling re-detect");
            MarkInactive();
            TriggerReconnect();
        }
    }

    private void TriggerReconnect()
    {
        var now = _timeProvider.GetUtcNow();
        if (now - _lastRetry < _retryInterval) return;
        _lastRetry = now;
        _onReconnect();
    }
}
