using System.Threading.Channels;
using SkiaSharp;

namespace ModernWigiDash.Sdk;

/// <summary>
/// The single frame-delivery policy module. Every transport hop — App WCF
/// sink, App direct-USB engine, Service <c>RunFrameLoop</c> — is an instance
/// of this module, so a backlog behaves identically in every mode:
///
/// bounded DropOldest channel → drain-to-latest (stale frames dropped, never
/// replayed) → paced send (default 33ms) → pooled buffers released.
///
/// Two entry points feed one policy: <see cref="Push"/> (composited bitmap;
/// encodes into a pooled exact-size buffer via the injected encoder) and
/// <see cref="PushBytes"/> (already-encoded bytes, e.g. the service hop that
/// receives frames over the pipe). Drop accounting is visible through
/// <see cref="DroppedCount"/>; the send seam is attached with
/// <see cref="AttachSend"/>.
/// </summary>
public sealed class FrameDelivery : IFrameSink
{
    private sealed record FrameSlot(byte[] Buffer, bool IsPooled);

    private readonly Channel<FrameSlot> _channel;
    private readonly FrameBufferPool? _pool;
    private readonly IRgb565Encoder? _encoder;
    private readonly TimeSpan _minInterval;
    private readonly Func<bool>? _isReady;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts;
    private readonly Action<string>? _log;

    private byte[]? _workBuffer;
    private volatile Func<byte[], bool>? _send;
    private DateTimeOffset _lastSendStart;
    private long _sent;
    private long _dropped;
    private int _disposed;

    /// <param name="encoder">Converts <see cref="SKBitmap"/> to RGB565 using a
    /// reusable work buffer. Required for <see cref="Push"/>; null is valid
    /// for byte-level instances (e.g. the service hop).</param>
    /// <param name="pool">Exact-size buffer pool. Required when
    /// <paramref name="encoder"/> is provided; the pool's buffers are the WCF
    /// serializer's exact-size requirement.</param>
    /// <param name="send">Initial send seam; can be rebound at runtime with
    /// <see cref="AttachSend"/> (null detaches).</param>
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
        _ = Task.Run(() => SenderLoop(_cts.Token), _cts.Token);
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

    /// <summary>
    /// Binds (or unbinds, with null) the transport send seam. Called when the
    /// service connects or faults; a null send makes the delivery not ready.
    /// </summary>
    public void AttachSend(Func<byte[], bool>? send) => _send = send;

    /// <summary>Encodes a composited frame into a pooled buffer and queues it.</summary>
    public FrameDeliveryResult Push(SKBitmap frame)
    {
        if (_encoder == null || _pool == null || _send == null || frame == null)
            return FrameDeliveryResult.Dropped;

        _encoder.Encode(frame, ref _workBuffer);
        if (_workBuffer == null)
            return FrameDeliveryResult.Dropped;

        byte[]? buffer = _pool.Acquire();
        if (buffer == null)
        {
            Interlocked.Increment(ref _dropped);
            return FrameDeliveryResult.Dropped;
        }

        Buffer.BlockCopy(_workBuffer, 0, buffer, 0, Math.Min(_workBuffer.Length, buffer.Length));
        return Queue(new FrameSlot(buffer, IsPooled: true));
    }

    /// <summary>Queues an already-encoded frame (e.g. the service hop).</summary>
    public FrameDeliveryResult PushBytes(byte[] frame)
    {
        if (_send == null || frame == null || frame.Length == 0)
            return FrameDeliveryResult.Dropped;
        return Queue(new FrameSlot(frame, IsPooled: false));
    }

    /// <inheritdoc />
    public FrameDeliveryResult SendFrame(SKBitmap frame) => Push(frame);

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
        if (slot.IsPooled && _pool != null)
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
            if (slot.IsPooled && _pool != null)
            {
                _pool.Release(slot.Buffer);
            }
        }

        _cts.Dispose();
        _send = null;
    }
}
