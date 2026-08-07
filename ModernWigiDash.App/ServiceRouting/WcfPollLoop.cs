using System;

namespace ModernWigiDash.App.ServiceRouting;

/// <summary>
/// One parameterized WCF poll loop. Owns its cancellation lifecycle, the
/// readiness guard, failure logging, and the inter-tick delay — the scaffold
/// that used to be copied into StartTouchPolling / StartSensorPolling /
/// StartFrameTimePolling. The probe is injected; the loop runs on a background
/// thread and calls the sample sink there (the touch sink marshals to the UI
/// thread itself).
/// </summary>
public sealed class WcfPollLoop : IDisposable
{
    private readonly string _name;
    private readonly TimeSpan _interval;
    private readonly Func<bool> _ready;
    private readonly Action _tick;
    private readonly Action _onTickFailure;
    private readonly Action<string> _log;
    private CancellationTokenSource? _cts;

    /// <param name="name">Log tag, e.g. "TOUCH".</param>
    /// <param name="interval">Delay between ticks.</param>
    /// <param name="ready">True when the probe can run (service active). While
    /// false the loop pauses at 500ms instead of hammering.</param>
    /// <param name="tick">One probe + sample sink; throws on failure.</param>
    /// <param name="onTickFailure">Failure observer (feeds readiness state).</param>
    /// <param name="log">Log sink.</param>
    public WcfPollLoop(string name, TimeSpan interval, Func<bool> ready, Action tick, Action onTickFailure, Action<string> log)
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
        _log($"[WCF] {_name} polling started ({(int)_interval.TotalMilliseconds}ms via WCF, background thread)");
        _ = Task.Run(() => Loop(ct), ct);
    }

    /// <summary>Stops the loop (idempotent).</summary>
    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_ready())
                {
                    try { await Task.Delay(500, ct); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                _tick();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log($"[WCF] {_name} poll failed: {ex.Message}");
                _onTickFailure();
            }

            try { await Task.Delay(_interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}
