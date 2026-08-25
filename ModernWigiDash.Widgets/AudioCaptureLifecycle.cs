namespace ModernWigiDash.Widgets;

/// <summary>
/// The audio visualizer's capture lifecycle: when a capture source is alive
/// and how it dies. Capture is tied to rendering: it starts on the first
/// <see cref="OnRender"/> (the widget's page becoming active) and stops when
/// renders stop being called for a grace period (page switched away); WASAPI
/// loopback capture would otherwise run forever in the background for a
/// hidden widget. The lock protocol, the stale-render watchdog, and the
/// deferred-stop marshaling (NAudio's capture-thread join lives beside this)
/// are owned here; the widget drives this module from Render and Dispose and
/// draws one snapshot.
/// </summary>
internal sealed class AudioCaptureLifecycle : IDisposable
{
    private readonly AudioFrameBuffer _buffer;
    private readonly Func<IAudioCaptureSource> _sourceFactory;
    private readonly Func<TimeProvider> _time;
    private readonly Func<int> _barCount;
    private readonly Action<string, Exception?> _logError;

    // Capture is touched from the render thread (start, timestamp), the
    // capture thread (the watchdog stop), and the thread pool (the deferred
    // NAudio dispose) — one lock serializes the start/stop sequences so a
    // watchdog firing as the page switches back can never unsubscribe/dispose
    // a source mid-start (a lost race self-heals on the next render tick,
    // which re-arms capture).
    private readonly Lock _lock = new();
    private volatile bool _capturing;
    private volatile bool _stopQueued;
    private IAudioCaptureSource? _source;

    // Primed to "now" at construction so the first callback can never measure
    // elapsed-since-epoch and kill a fresh capture; OnRender re-primes it
    // before every start. The render thread writes it and the capture thread
    // reads it (an aligned long access is atomic on this architecture).
    private long _lastRenderTimestamp = TimeProvider.System.GetTimestamp();

    public AudioCaptureLifecycle(
        AudioFrameBuffer buffer,
        Func<IAudioCaptureSource> sourceFactory,
        Func<TimeProvider> time,
        Func<int> barCount,
        Action<string, Exception?> logError)
    {
        _buffer = buffer;
        _sourceFactory = sourceFactory;
        _time = time;
        _barCount = barCount;
        _logError = logError;
    }

    /// <summary>A render tick: prime the watchdog timestamp and ensure
    /// capture is running. A failed start stays stopped and retries on the
    /// next tick.</summary>
    public void OnRender()
    {
        _lastRenderTimestamp = _time().GetTimestamp();
        EnsureCapture();
    }

    /// <summary>Stops capture and releases the source. Idempotent and safe
    /// from any thread (the render tick, the watchdog's deferred stop, and
    /// the widget's dispose all route through here).</summary>
    public void Stop()
    {
        if (!_capturing) return;
        StopCapture();
    }

    public void Dispose() => Stop();

    private void EnsureCapture()
    {
        if (_capturing) return;

        lock (_lock)
        {
            if (_capturing) return;
            if (!TryStartCapture()) return;
            _capturing = true;
        }
    }

    private bool TryStartCapture()
    {
        IAudioCaptureSource? source = null;
        try
        {
            source = _sourceFactory();
            source.SamplesAvailable += OnSamplesAvailable;
            source.Start();
            _source = source;
            return true;
        }
        catch (Exception ex)
        {
            // A half-opened source is disposed here (the _source slot is
            // empty while !capturing, so disposing it would be a no-op that
            // leaks the device handle); the error is logged and the next
            // render tick retries the start.
            try
            {
                source?.Dispose();
            }
            catch
            {
                // A failing source can fail to dispose; the start error is
                // already logged below.
            }
            _logError("Failed to initialize audio capture", ex);
            return false;
        }
    }

    private void StopCapture()
    {
        lock (_lock)
        {
            _stopQueued = false;
            var source = _source;
            _source = null;
            _capturing = false;
            if (source is null) return;

            try
            {
                source.SamplesAvailable -= OnSamplesAvailable;
                source.Dispose();
            }
            catch (Exception ex)
            {
                _logError("Failed to stop audio capture", ex);
            }
        }
    }

    private void OnSamplesAvailable(float[] samples)
    {
        // Watchdog: when the widget is no longer rendered (page switched
        // away), stop capture instead of running forever. _lastRenderTimestamp
        // is primed before capture starts, so the first callback cannot kill a
        // fresh capture.
        //
        // NAudio raises DataAvailable from inside ReadNextPacket on the capture
        // thread, and WasapiCapture.Dispose joins that same thread. Stopping
        // synchronously here would self-join and deadlock the capture thread
        // while it holds the capture lock, blocking the render thread on that
        // lock forever. The stop is therefore deferred to the thread pool and
        // the lock still serializes it against a concurrent start.
        if (_time().GetElapsedTime(_lastRenderTimestamp).TotalSeconds > 1.0)
        {
            // Queue at most one deferred stop; the first work item that runs
            // resets the flag (under the lock) before disposing.
            if (!_stopQueued)
            {
                _stopQueued = true;
                ThreadPool.QueueUserWorkItem(_ => StopCapture());
            }
            return;
        }

        _buffer.Feed(samples, _buffer.ClampBars(_barCount()));
    }
}
