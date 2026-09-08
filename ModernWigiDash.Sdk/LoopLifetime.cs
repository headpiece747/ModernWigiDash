namespace ModernWigiDash.Sdk;

/// <summary>
/// The shared lifetime module for background loops: owns the gate, the CTS
/// handoff, the bounded join, and the timed-out verdict. PollLoop, FeedLoop,
/// and FrameDelivery's sender loop all route through this instead of spelling
/// the same cancel / bounded-join / CTS-handoff dance three times.
/// </summary>
internal sealed class LoopLifetime : IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private int _disposed;
    private TimeSpan _joinTimeout;

    /// <summary>Owns a background loop task's bounded shutdown: the join waits a
    /// fixed budget for the task to unwind, then abandons it rather than stalling.</summary>
    /// <param name="joinTimeout">The bounded wait for the loop task to unwind.</param>
    public LoopLifetime(TimeSpan joinTimeout)
    {
        _joinTimeout = joinTimeout;
    }

    /// <summary>Starts the loop (idempotent). Returns the token to run with.</summary>
    public CancellationToken Start(Func<CancellationToken, Task> body)
    {
        lock (_gate)
        {
            if (_disposed != 0 || _cts != null) return CancellationToken.None;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _task = Task.Run(() => body(ct), ct);
            return ct;
        }
    }

    /// <summary>Stops the loop (idempotent). Cancels but deliberately does NOT
    /// dispose the token source here: the loop task may still be draining when
    /// Stop runs, and disposing a CTS a running task still holds can fault the
    /// task's cancellation registration. The stopped source is handed to
    /// <see cref="Dispose"/>, which owns its disposal.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_cts is null) return;
            _cts.Cancel();
            _cts = null;
        }
    }

    /// <summary>
    /// Cancels the loop, waits (bounded) for the loop task to unwind, then
    /// disposes the stopped token source. The join matters: a tick in flight
    /// may be mid-probe against a resource the caller frees right after Dispose
    /// returns — returning past a live tick would hand the freed handles to the
    /// background thread.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? cts = null;
        Task? task;
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            if (_cts != null)
            {
                _cts.Cancel();
                cts = _cts;
                _cts = null;
            }
            task = _task;
        }
        try
        {
            // Bounded wait for the loop task to unwind; the timeout is the
            // cancellation, so opt out of token-based cancellation explicitly.
            // Normally fast: a cancelled loop exits at the next await point.
            task?.Wait(_joinTimeout, CancellationToken.None);
        }
        catch
        {
            // Loop task already faulted/cancelled — teardown is best-effort
        }
        cts?.Dispose();
    }
}
