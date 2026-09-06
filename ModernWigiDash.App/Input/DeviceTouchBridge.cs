using SkiaSharp;
using System.Windows.Threading;

namespace ModernWigiDash.App.Input;

/// <summary>
/// The device-touch bridge: owns the queue, lock, drain scheduling, and
/// rejection unpoison. The window forwards one call (Enqueue) and the bridge
/// feeds the input controller in order on the dispatcher thread.
/// </summary>
internal sealed class DeviceTouchBridge
{
    private readonly Queue<(float X, float Y, TouchEventType Type)> _queue = new();
    private readonly Lock _lock = new();
    private bool _drainScheduled;
    private readonly Dispatcher _dispatcher;
    private readonly Action<float, float, TouchEventType> _feedSink;
    private readonly Action _drainCallback;

    /// <param name="dispatcher">The WPF dispatcher to marshal onto.</param>
    /// <param name="feedSink">The sink that receives each touch event in order
    /// (the input controller's Press/Move/Release).</param>
    public DeviceTouchBridge(Dispatcher dispatcher, Action<float, float, TouchEventType> feedSink)
    {
        _dispatcher = dispatcher;
        _feedSink = feedSink;
        _drainCallback = Drain;
    }

    /// <summary>
    /// Enqueues a touch event from the engine's poll thread. Schedules one
    /// drain per burst on the dispatcher thread.
    /// </summary>
    public void Enqueue(float x, float y, TouchEventType type)
    {
        bool schedule;
        lock (_lock)
        {
            _queue.Enqueue((x, y, type));
            schedule = !_drainScheduled;
            _drainScheduled = true;
        }
        if (schedule)
        {
            try
            {
                _ = _dispatcher.BeginInvoke(_drainCallback);
            }
            catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException)
            {
                // A shutting-down dispatcher rejects the post: unpoison the
                // flag so the next event schedules a fresh drain (a redundant
                // second drain is harmless — it runs to empty).
                lock (_lock)
                {
                    _drainScheduled = false;
                }
            }
        }
    }

    private void Drain()
    {
        // Snapshot the burst under the lock, release, then feed: an input
        // pass that held the lock would make the poll thread's next Enqueue
        // wait out the whole gesture feed (the old deliberate trade, retired).
        List<(float, float, TouchEventType)> batch;
        lock (_lock)
        {
            _drainScheduled = false;
            batch = new List<(float, float, TouchEventType)>(_queue.Count);
            while (_queue.Count > 0)
                batch.Add(_queue.Dequeue());
        }

        foreach (var (x, y, type) in batch)
        {
            _feedSink(x, y, type);
        }
    }

    /// <summary>Drains the queue synchronously on the current thread.
    /// Internal so tests can drive one deterministic drain without pumping
    /// the dispatcher.</summary>
    internal void DrainForTest() => Drain();
}
