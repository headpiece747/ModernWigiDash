using System.Windows.Threading;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// Owns the 30 FPS presentation cadence: a dispatcher timer that composes the
/// active page, hands the freshly composed frame to the presenter, and
/// requests a repaint so the window draws the same buffer it sent. The window
/// keeps only the WPF draw; compose, send, and timing live here. The
/// compose/send step is injected so tests can drive the cadence with recording
/// delegates instead of a compositor and USB engine.
/// </summary>
public sealed class FramePump : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Action _composeAndSend;
    private readonly Action _requestRepaint;
    private readonly Action? _onTick;
    private int _disposed;

    /// <param name="composeAndSend">Composes the active page and queues the
    /// frame to the presenter. Runs once per tick, before the repaint, so the
    /// buffer the window draws is the same one that was sent.</param>
    /// <param name="requestRepaint">Asks the window to redraw the composed
    /// buffer (e.g. <c>InvalidateVisual</c>).</param>
    /// <param name="onTick">Optional per-tick callback (e.g. badge updates).</param>
    /// <param name="interval">Tick interval; defaults to the shared 30 FPS
    /// cadence (<see cref="FrameDelivery.FrameInterval"/> — the single
    /// frame-rate owner).</param>
    public FramePump(Action composeAndSend, Action requestRepaint, Action? onTick = null, TimeSpan? interval = null)
    {
        _composeAndSend = composeAndSend;
        _requestRepaint = requestRepaint;
        _onTick = onTick;
        _timer = new DispatcherTimer { Interval = interval ?? FrameDelivery.FrameInterval };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _timer.Stop();
    }

    /// <summary>
    /// One pump tick: compose → send → repaint → badge. The disposed guard
    /// exists because <see cref="DispatcherTimer.Stop"/> cannot cancel a tick
    /// already queued in the dispatcher — a tick queued just before Dispose
    /// would otherwise run after teardown and compose onto disposed state.
    /// Internal so tests can drive the exact queued-tick-after-dispose race.
    /// </summary>
    internal void Tick()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _composeAndSend();
        _requestRepaint();
        _onTick?.Invoke();
    }
}
