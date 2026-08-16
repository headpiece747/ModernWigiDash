namespace ModernWigiDash.Sdk;

/// <summary>
/// One parameterized poll loop. Owns its cancellation lifecycle, the readiness
/// guard, failure logging, and the inter-tick delay — the scaffold both sides
/// of the pipe used to copy by hand. The probe is injected; the loop runs on a
/// background thread and calls the sample sink there (sinks that need another
/// thread marshal themselves).
///
/// Used by the App's two 1s direct producers (LibreHardwareService sensors,
/// PresentMon frame-time) and by the DisplayDeviceEngine's 16ms direct-USB
/// touch loop — one loop shape, every hop.
/// </summary>
public sealed class PollLoop : IDisposable
{
    private readonly string _name;
    private readonly TimeSpan _interval;
    private readonly Func<bool> _ready;
    private readonly Action _tick;
    private readonly Action _onTickFailure;
    private readonly Action<string> _log;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _stoppedCts;
    private Task? _loopTask;
    // The failure-message dedup rule, owned once (the LogOnChange module —
    // the hand-rolled field comparison this used to carry is the same rule).
    private readonly LogOnChange _failureDedup = new();

    /// <param name="name">Log tag, e.g. "TOUCH".</param>
    /// <param name="interval">Delay between ticks.</param>
    /// <param name="ready">True when the probe can run. While false the loop
    /// pauses at 500ms instead of hammering.</param>
    /// <param name="tick">One probe + sample sink; throws on failure.</param>
    /// <param name="onTickFailure">Failure observer (feeds readiness state).</param>
    /// <param name="log">Log sink.</param>
    public PollLoop(string name, TimeSpan interval, Func<bool> ready, Action tick, Action onTickFailure, Action<string> log)
    {
        _name = name;
        _interval = interval;
        _ready = ready;
        _tick = tick;
        _onTickFailure = onTickFailure;
        _log = log;
    }

    /// <summary>Starts the loop (idempotent).</summary>
    public void Start()
    {
        if (_cts != null) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _log($"[{_name}] polling started ({(int)_interval.TotalMilliseconds}ms, background thread)");
        _loopTask = Task.Run(() => Loop(ct), ct);
    }

    /// <summary>Stops the loop (idempotent). Cancels but deliberately does NOT
    /// dispose the token source here: the loop task may still be draining when
    /// Stop runs, and disposing a CTS a running task still holds can fault the
    /// task's cancellation registration. The stopped source is handed to
    /// <see cref="Dispose"/>, which owns its disposal.</summary>
    public void Stop()
    {
        if (_cts is null) return;
        _cts.Cancel();
        _stoppedCts = _cts;
        _cts = null;
    }

    /// <summary>
    /// Cancels the loop, waits (bounded) for the loop task to unwind, then
    /// disposes the stopped token source. The join matters: a tick in flight
    /// may be mid-probe against a resource the caller frees right after Dispose
    /// returns (the PresentMon native session on close) — returning past a
    /// live tick would hand the freed handles to the background thread.
    /// </summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts = null;
        try
        {
            // Bounded wait for the loop task to unwind; the timeout is the
            // cancellation, so opt out of token-based cancellation explicitly.
            // Normally fast: a cancelled loop exits at the next await point.
            _loopTask?.Wait(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch
        {
            // Loop task already faulted/cancelled — teardown is best-effort
        }
        _stoppedCts?.Dispose();
        _stoppedCts = null;
    }

    private async Task Loop(CancellationToken ct)
    {
        // One timer for the loop's lifetime instead of a Task.Delay per tick
        // (the 16ms touch poll would otherwise churn ~60 timers/sec).
        using var timer = new PeriodicTimer(_interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_ready())
                {
                    try { await Task.Delay(500, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                _tick();
                _failureDedup.Changed(null); // a successful tick resets the dedup
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_failureDedup.Changed(ex.Message))
                {
                    _log($"[{_name}] poll failed: {ex.Message}");
                }
                _onTickFailure();
            }

            try { await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }
}
