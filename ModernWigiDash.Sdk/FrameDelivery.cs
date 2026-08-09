using System.Threading.Channels;
using SkiaSharp;

namespace ModernWigiDash.Sdk;

/// <summary>
/// The single frame-delivery policy module. The App binds one instance to the
/// direct-USB engine, so a backlog behaves identically in every mode:
///
/// bounded DropOldest channel → drain-to-latest (stale frames dropped, never
/// replayed) → paced send (default 33ms) → pooled buffers released.
///
/// One entry point feeds the policy: <see cref="Push"/> (composited bitmap;
/// encodes into a pooled exact-size buffer via the injected encoder). Drop
/// accounting is visible through <see cref="DroppedCount"/>.
/// </summary>
public sealed class FrameDelivery : IDisposable
{
    private sealed record FrameSlot(byte[] Buffer);

    private readonly Channel<FrameSlot> _channel;
    private readonly FrameBufferPool? _pool;
    private readonly IRgb565Encoder? _encoder;
    private readonly TimeSpan _minInterval;
    private readonly Func<bool>? _isReady;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts;
    private readonly Action<string>? _log;
    private readonly Task _senderTask;

    private readonly Func<byte[], bool>? _send;
    private DateTimeOffset _lastSendStart;
    private long _sent;
    private int _sentLogCount;
    private long _dropped;
    private int _disposed;

    /// <param name="encoder">Converts <see cref="SKBitmap"/> to RGB565 using a
    /// reusable work buffer. Required for <see cref="Push"/>.</param>
    /// <param name="pool">Exact-size buffer pool. Required when
    /// <paramref name="encoder"/> is provided.</param>
    /// <param name="send">Send seam to the transport, bound once at
    /// construction.</param>
    /// <param name="isReady">Optional readiness predicate. Defaults to "a send
    /// seam is attached".</param>
    /// <param name="minInterval">Minimum interval between transport sends
    /// (default 33ms ≈ 30 FPS, the device capability).</param>
    /// <param name="timeProvider">Clock for pacing; tests substitute a fake.</param>
    /// <param name="capacity">Bounded channel capacity (DropOldest).</param>
    /// <param name="log">Optional log sink for send/drop lines.</param>
    public FrameDelivery(
        IRgb565Encoder? encoder = null,
        FrameBufferPool? pool = null,
        Func<byte[], bool>? send = null,
        Func<bool>? isReady = null,
        TimeSpan? minInterval = null,
        TimeProvider? timeProvider = null,
        int capacity = 2,
        Action<string>? log = null)
    {
        if (encoder != null && pool == null)
            throw new ArgumentException("A buffer pool is required when an encoder is provided.", nameof(pool));

        _encoder = encoder;
        _pool = pool;
        _minInterval = minInterval ?? TimeSpan.FromMilliseconds(33);
        _isReady = isReady;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _send = send;
        _log = log;
        _channel = Channel.CreateBounded<FrameSlot>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
        _cts = new CancellationTokenSource();
        _senderTask = Task.Run(() => SenderLoop(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// True when this delivery can currently route frames: the readiness
    /// predicate (when provided) or simply that a send seam is attached.
    /// </summary>
    public bool IsReady => _isReady?.Invoke() ?? _send != null;

    /// <summary>Frames successfully handed to the transport.</summary>
    public long FramesSent => Interlocked.Read(ref _sent);

    /// <summary>
    /// Frames rejected by the pipeline (pool exhausted, no encoder) plus stale
    /// buffered frames dropped by the coalescer during a backlog.
    /// </summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Encodes a composited frame directly into a pooled buffer and queues it.</summary>
    public FrameDeliveryResult Push(SKBitmap frame)
    {
        if (_encoder == null || _pool == null || _send == null || frame == null)
            return FrameDeliveryResult.Dropped;
        if (_isReady?.Invoke() == false)
            return FrameDeliveryResult.Dropped;

        byte[]? buffer = _pool.Acquire();
        if (buffer == null)
        {
            Interlocked.Increment(ref _dropped);
            return FrameDeliveryResult.Dropped;
        }

        try
        {
            _encoder.Encode(frame, buffer);
        }
        catch
        {
            _pool.Release(buffer);
            throw;
        }

        return Queue(new FrameSlot(buffer));
    }

    /// <summary>
    /// Drains the channel, keeps only the latest frame (returning every stale
    /// frame's pooled buffer), paces to the minimum interval, and sends.
    /// </summary>
    private async Task SenderLoop(CancellationToken ct)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(ct))
            {
                FrameSlot? latest = ChannelFrameCoalescer.DrainToLatest(
                    _channel.Reader,
                    slot => ReleaseSlot(slot, dropped: true));

                if (latest == null) continue;

                // Pace from the START of the previous send: the interval caps
                // the frame rate, so a slow transport must not be charged the
                // full interval on top of its own write time.
                var now = _timeProvider.GetUtcNow();
                var sinceLastStart = now - _lastSendStart;
                if (sinceLastStart < _minInterval)
                {
                    try
                    {
                        await Task.Delay(_minInterval - sinceLastStart, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        ReleaseSlot(latest, dropped: false);
                        break;
                    }
                }

                _lastSendStart = _timeProvider.GetUtcNow();

                try
                {
                    bool ok = _send?.Invoke(latest.Buffer) == true;
                    if (ok)
                    {
                        Interlocked.Increment(ref _sent);
#pragma warning disable S125 // log-cadence documentation, not commented-out code
                        // Per-frame success log would grow unbounded at ~30/s;
                        // mirror DisplayHidTransport's % 60 diagnostic cadence.
                        // (Drops are counted in DroppedCount, not logged.)
#pragma warning restore S125
                        if (Interlocked.Increment(ref _sentLogCount) % 60 == 0)
                            _log?.Invoke($"[FrameDelivery] Frame #{Volatile.Read(ref _sent)} sent ({latest.Buffer.Length} bytes)");
                    }
                    else
                    {
                        _log?.Invoke($"[FrameDelivery] Send failed (buffer={latest.Buffer.Length} bytes)");
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"[FrameDelivery] Send exception: {ex.Message}");
                }
                finally
                {
                    ReleaseSlot(latest, dropped: false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: sender loop cancelled during shutdown
        }
    }

    private void ReleaseSlot(FrameSlot slot, bool dropped)
    {
        if (dropped)
        {
            Interlocked.Increment(ref _dropped);
        }
        if (_pool != null)
        {
            _pool.Release(slot.Buffer);
        }
    }

    private FrameDeliveryResult Queue(FrameSlot slot)
    {
        // DropOldest never fails; the coalescer owns stale-frame dropping.
        _channel.Writer.TryWrite(slot);
        return FrameDeliveryResult.Queued;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();

        // Return any pooled buffers still queued in the channel before the
        // sender loop exits (they would otherwise be stranded at close).
        while (_channel.Reader.TryRead(out var slot))
        {
            if (_pool != null)
            {
                _pool.Release(slot.Buffer);
            }
        }

        // Join the sender loop with a bounded wait: a send is a synchronous
        // USB write with up to a 30s timeout, so never block close on it —
        // but do give a clean loop exit the chance to release its in-flight
        // slot before the transport is disposed underneath it. The token is
        // already cancelled above, so passing it would abort the join
        // immediately — the bounded wait is the whole point.
        try
        {
            _senderTask.Wait(TimeSpan.FromSeconds(1), CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected: the loop exited via cancellation.
        }
        catch (AggregateException)
        {
            // The loop faulted after cancellation; nothing left to join.
        }

        _cts.Dispose();
    }
}
