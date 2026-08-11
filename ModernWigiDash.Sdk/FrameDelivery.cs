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
/// encodes into a pooled exact-size buffer via the injected encoder). Verdict
/// accounting is visible through <see cref="DroppedCount"/> (split into pool
/// exhaustion vs. coalescer drops) and <see cref="SendFailedCount"/> — a dead
/// pipe shows as send failures, never as silent success or drops.
/// </summary>
public sealed class FrameDelivery : IDisposable
{
    /// <summary>The presentation cadence (30 FPS) - the single owner of the frame
    /// rate, consumed by the delivery pacing and the App's FramePump tick so
    /// the two can never disagree.</summary>
    public const double FramesPerSecond = 30.0;

    /// <summary>The pacing interval for one frame (1000/30 ms).</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000.0 / FramesPerSecond);

    private readonly record struct FrameSlot(byte[] Buffer);

    private readonly Channel<FrameSlot> _channel;
    private readonly FrameBufferPool? _pool;
    private readonly IRgb565Encoder? _encoder;
    private readonly TimeSpan _minInterval;
    private readonly Func<bool>? _isReady;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _cts;
    private readonly Task _senderTask;

    private readonly Func<byte[], bool>? _send;
    private DateTimeOffset _lastSendStart;
    private int _sendInFlight;
    private long _sent;
    private long _dropped;
    private long _droppedPool;
    private long _droppedCoalesced;
    private long _sendFailed;
    private int _disposed;

    /// <param name="encoder">Converts <see cref="SKBitmap"/> to RGB565 using a
    /// reusable work buffer. Required for <see cref="Push"/>; the buffer pool
    /// is built from its <see cref="IRgb565Encoder.OutputBufferSize"/>.</param>
    /// <param name="send">Send seam to the transport, bound once at
    /// construction.</param>
    /// <param name="isReady">Optional readiness predicate. Defaults to "a send
    /// seam is attached".</param>
    /// <param name="minInterval">Minimum interval between transport sends
    /// (default 33ms ≈ 30 FPS, the device capability).</param>
    /// <param name="timeProvider">Clock for pacing; tests substitute a fake.</param>
    /// <param name="capacity">Bounded channel capacity (DropOldest). The buffer
    /// pool pre-allocates capacity + 1 — the in-flight maximum is the channel
    /// (capacity) plus the sender's held slot (1), so the pool must cover both
    /// or backlog pressure would exhaust it before the coalescer drops.</param>
    /// <param name="log">Optional log sink for send/drop lines.</param>
    internal FrameDelivery(
        IRgb565Encoder? encoder = null,
        Func<byte[], bool>? send = null,
        Func<bool>? isReady = null,
        TimeSpan? minInterval = null,
        TimeProvider? timeProvider = null,
        int capacity = 4,
        Action<string>? log = null)
    {
        if (encoder is not null)
        {
            // The pool is sized from the encoder's output — an exact-size pool
            // that disagrees with the encoder (whose releases would be
            // silently discarded) is unrepresentable by construction. The +1
            // margin covers the sender's in-flight slot: while the transport
            // is stalled, the channel holds up to `capacity` frames and the
            // sender holds one more, so a pool of exactly `capacity` would
            // exhaust (DroppedPoolCount) before the coalescer ever saw the
            // backlog. Sized capacity + 1, backlog drops stay coalescer drops
            // until the channel itself is full.
            _pool = new FrameBufferPool(encoder.OutputBufferSize, capacity + 1);
        }

        _encoder = encoder;
        _minInterval = minInterval ?? FrameInterval;
        _isReady = isReady;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _send = send;
        // The DiagLog write seam is the injected log callback with a
        // null-tolerant no-op — deliberately NOT DiagLog's FileLog fallback, so
        // a delivery without a log sink stays silent.
        _sentLog = new DiagLog("FrameDelivery", 60, write: log ?? (static _ => { }));
        _sendFailLog = new DiagLog("FrameDelivery", 60, logFirst: true, write: log ?? (static _ => { }));
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
    /// Creates a fully configured delivery with the policy's required seams.
    /// Prefer this over the constructor at production bind sites — the encode
    /// and send seams are required, so an unconfigured delivery (which would
    /// silently drop every frame) is unrepresentable. The internal constructor
    /// remains for tests that intentionally exercise unconfigured readiness
    /// semantics.
    /// </summary>
    public static FrameDelivery Create(
        IRgb565Encoder encoder,
        Func<byte[], bool> send,
        Func<bool>? isReady = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ArgumentNullException.ThrowIfNull(send);
        return new FrameDelivery(encoder, send, isReady, log: log);
    }

    /// <summary>
    /// True while the sender loop is inside the transport write. A compose
    /// caller (the FramePump gate) skips composing a new frame during the
    /// write — the display can't take it anyway, so the encode is dead CPU.
    /// </summary>
    public bool IsSendInFlight => Volatile.Read(ref _sendInFlight) != 0;

    /// <summary>
    /// True when this delivery can currently route frames: the readiness
    /// predicate (when provided) or simply that a send seam is attached.
    /// </summary>
    public bool IsReady => _isReady?.Invoke() ?? _send is not null;

    /// <summary>Frames successfully handed to the transport. Instrumentation
    /// (also feeds the log cadence): the delivery pipeline is the single owner
    /// of frame accounting. The transport keeps only a bulk-layer diagnostic
    /// counter for its own failure log — never a second delivery accounting.</summary>
    public long FramesSent => Interlocked.Read(ref _sent);

    /// <summary>
    /// Frames dropped inside the pipeline: pool exhaustion at push time,
    /// channel rejects (a push after disposal), plus stale buffered frames
    /// dropped by the coalescer during a backlog. Push-time rejections that
    /// return before the pipeline is reached — no encoder/pool/send seam
    /// attached, a null frame, or the readiness predicate false — are NOT
    /// counted here. The two in-pipeline drop sources are also visible
    /// separately via <see cref="DroppedPoolCount"/> and
    /// <see cref="DroppedCoalescedCount"/>.
    /// </summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Frames dropped at push time because the buffer pool was
    /// exhausted — backlog pressure, the producer outran the sender.</summary>
    public long DroppedPoolCount => Interlocked.Read(ref _droppedPool);

    /// <summary>Stale buffered frames the coalescer discarded while draining a
    /// backlog (drain-to-latest replays only the newest frame).</summary>
    public long DroppedCoalescedCount => Interlocked.Read(ref _droppedCoalesced);

    /// <summary>Frames handed to the transport seam that failed to send (the
    /// seam returned false or threw). A broken pipe accumulates here, not in
    /// <see cref="FramesSent"/> and not in the drop counters.</summary>
    public long SendFailedCount => Interlocked.Read(ref _sendFailed);

    /// <summary>Encodes a composited frame directly into a pooled buffer and queues it.</summary>
    public FrameDeliveryResult Push(SKBitmap frame)
    {
        if (_encoder is null || _pool is null || _send is null || frame is null)
            return FrameDeliveryResult.Dropped;
        if (_isReady?.Invoke() == false)
            return FrameDeliveryResult.Dropped;

        byte[]? buffer = _pool.Acquire();
        if (buffer is null)
        {
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _droppedPool);
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
                FrameSlot latest = ChannelFrameCoalescer.DrainToLatest(
                    _channel.Reader,
                    slot => ReleaseSlot(slot, dropped: true));
                if (latest.Buffer is null) continue;

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
                    // The in-flight flag lets a compose-only caller (the
                    // FramePump gate) skip work while the display is still
                    // writing — the bulk write (~55ms) outruns the 33ms tick,
                    // so composing during it is dead CPU.
                    Volatile.Write(ref _sendInFlight, 1);
                    bool ok = _send?.Invoke(latest.Buffer) == true;
                    if (ok)
                    {
                        long sent = Interlocked.Increment(ref _sent);
#pragma warning disable S125 // log-cadence documentation, not commented-out code
                        // Per-frame success log would grow unbounded at ~30/s;
                        // every-60th cadence keeps it bounded.
                        // (Drops are counted in DroppedCount, not logged.)
#pragma warning restore S125
                        _sentLog.Write(() => $"Frame #{sent} sent ({latest.Buffer.Length} bytes)");
                    }
                    else
                    {
                        Interlocked.Increment(ref _sendFailed);
                        _sendFailLog.Write($"Send failed (buffer={latest.Buffer.Length} bytes)");
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _sendFailed);
                    _sendFailLog.Write($"Send exception: {ex.Message}");
                }
                finally
                {
                    Volatile.Write(ref _sendInFlight, 0);
                    ReleaseSlot(latest, dropped: false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: sender loop cancelled during shutdown
        }
    }

    /// <summary>
    /// Log cadences composed as <see cref="DiagLog"/>s: success logs every 60th
    /// frame; failure logs the first occurrence and then every 60th — a dead
    /// bus cannot spam the log at ~30 lines/s. The write seam is the injected
    /// log callback (null = write nothing), so the tag and cadence rules are
    /// declared once instead of hand-baked at each call site.
    /// </summary>
    private readonly DiagLog _sentLog;
    private readonly DiagLog _sendFailLog;

    private void ReleaseSlot(FrameSlot slot, bool dropped)
    {
        if (dropped)
        {
            Interlocked.Increment(ref _dropped);
            Interlocked.Increment(ref _droppedCoalesced);
        }
        if (_pool is not null)
        {
            _pool.Release(slot.Buffer);
        }
    }

    private FrameDeliveryResult Queue(FrameSlot slot)
    {
        // A DropOldest channel only rejects writes once it is completed;
        // stale-frame dropping is the coalescer's job while it is open.
        if (_channel.Writer.TryWrite(slot))
        {
            return FrameDeliveryResult.Queued;
        }

        ReleaseSlot(slot, dropped: true);
        return FrameDeliveryResult.Dropped;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _channel.Writer.TryComplete();

        // Return any pooled buffers still queued in the channel before the
        // sender loop exits (they would otherwise be stranded at close).
        while (_channel.Reader.TryRead(out var slot))
        {
            if (_pool is not null)
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

        // Dispose the token source only once the sender loop has exited — a
        // send still in flight (up to 30s USB timeout) may hold the token, and
        // disposing a source a running task still references can fault its
        // cancellation registration. When the bounded join above timed out,
        // the source is deliberately dropped with the object instead.
        if (_senderTask.IsCompleted)
        {
            _cts.Dispose();
        }
    }
}
