using ModernWigiDash.Hardware.Transport;

namespace ModernWigiDash.Tests;

[TestClass]
public class FrameDeliveryTests
{
    // ── readiness / seam binding ───────────────────────────

    [TestMethod]
    public void IsReady_WhenNoSendAttached_IsFalse()
    {
        using var delivery = new FrameDelivery();

        Assert.IsFalse(delivery.IsReady);
    }

    [TestMethod]
    public void Push_WhenNoSendAttached_ReturnsDropped()
    {
        using var delivery = new FrameDelivery();
        using var bitmap = CreateFrameBitmap();

        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery.Push(bitmap));
    }

    [TestMethod]
    public void IsReady_WhenSendProvidedInCtor_IsTrue()
    {
        using var delivery = new FrameDelivery(send: _ => FrameSendResult.Sent);

        Assert.IsTrue(delivery.IsReady);
    }

    [TestMethod]
    public void IsReady_ReadinessPredicateOverridesDefault()
    {
        using var delivery = new FrameDelivery(send: _ => FrameSendResult.Sent, isReady: () => false);

        Assert.IsFalse(delivery.IsReady);
    }

    // ── the compose gate: in-flight flag ────────────────────

    [TestMethod]
    public void IsSendInFlight_TrueDuringSend_AndFalseAfter()
    {
        var observed = new List<bool>();
        var release = new ManualResetEventSlim(false);
        FrameDelivery? delivery = null;
        delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ =>
            {
                observed.Add(delivery!.IsSendInFlight);
                release.Set();
                return FrameSendResult.Sent;
            });
        using var owned = delivery;
        using var bitmap = CreateFrameBitmap();

        Assert.IsFalse(delivery.IsSendInFlight, "Idle delivery must not report an in-flight send");
        Assert.AreEqual(FrameDeliveryResult.Queued, delivery.Push(bitmap));

        Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)), "The sender must reach the send callback");
        Assert.IsTrue(observed.Count == 1 && observed[0], "The flag must read true inside the send callback");
        // The flag clears in the sender's finally — wait for the release to unwind.
        TestWait.WaitUntilAsync(() => !delivery.IsSendInFlight, TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert.IsFalse(delivery.IsSendInFlight, "The flag must clear after the send completes");
    }

    // ── Push (SKBitmap): encode → pooled buffer → deliver ──

    [TestMethod]
    public void Push_WithoutEncoder_ReturnsDropped()
    {
        using var delivery = new FrameDelivery(send: _ => FrameSendResult.Sent);
        using var bitmap = CreateFrameBitmap();

        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery.Push(bitmap));
    }

    [TestMethod]
    public void Push_WhenEncoderThrows_DropsAndSurvives()
    {
        var encoder = new FixedSizeEncoder(4096);
        encoder.SetThrowOnEncode(true);
        var logs = new List<string>();
        using var delivered = new ManualResetEventSlim(false);
        using var delivery = new FrameDelivery(encoder: encoder, send: _ => { delivered.Set(); return FrameSendResult.Sent; }, log: logs.Add);
        using var bitmap = CreateFrameBitmap();

        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery.Push(bitmap), "An encode failure must drop the frame, not escape the tick");
        Assert.AreEqual(1L, delivery.DroppedEncodeCount);
        Assert.IsTrue(logs.Any(line => line.Contains("encode failed")), "The encode failure must surface through the log seam");

        // The pipeline must survive: the SAME delivery that dropped the frame
        // must deliver after the encoder recovers (the production try/catch in
        // Push guarantees survival — a fresh pipeline would prove nothing).
        encoder.SetThrowOnEncode(false);
        delivered.Reset();
        Assert.AreEqual(FrameDeliveryResult.Queued, delivery.Push(bitmap));
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "The delivery must recover after a failed encode");
    }

    [TestMethod]
    public void Push_WithEncoderAndSend_DeliversEncodedRgb565Frame()
    {
        using var delivered = new ManualResetEventSlim(false);
        byte[]? received = null;
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: bytes =>
            {
                received = bytes;
                delivered.Set();
                return FrameSendResult.Sent;
            });
        using var bitmap = CreateFrameBitmap();

        FrameDeliveryResult result = delivery.Push(bitmap);

        Assert.AreEqual(FrameDeliveryResult.Queued, result, "Ready delivery must accept the frame");
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "Sender loop must deliver the frame");
        Assert.IsNotNull(received);
        Assert.AreEqual(DisplayProtocolConstants.FrameBufferSize, received.Length);
    }

    [TestMethod]
    public async Task Push_AfterDelivery_ReturnsBufferToPool()
    {
        using var delivered = new ManualResetEventSlim(false);
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ =>
            {
                delivered.Set();
                return FrameSendResult.Sent;
            },
            capacity: 1);
        using var bitmap = CreateFrameBitmap();

        delivery.Push(bitmap);
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "Sender loop must deliver the frame");

        // Pool of 1: the second push only queues once the first buffer is back
        // in the pool. The release lands right after the send, so keep pushing
        // until one queues — a drop is the "buffer still in flight" signal.
        delivered.Reset();
        await TestWait.WaitUntilAsync(() => delivery.Push(bitmap) == FrameDeliveryResult.Queued, TimeSpan.FromSeconds(2));
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "The released buffer must deliver a second frame");
    }

    [TestMethod]
    public void Push_WhenPoolExhausted_ReturnsDroppedAndCounts()
    {
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ => FrameSendResult.Failed,
            capacity: 1);
        using var bitmap = CreateFrameBitmap();

        // First push: buffer acquired, send fails, buffer released back.
        delivery.Push(bitmap);
        // Second push: the loop may have released the buffer already; force the
        // no-buffer path by exhausting with a blocking send.
        Assert.AreEqual(0, delivery.DroppedCount);

        // Pool of 2 (capacity 1 + the sender's in-flight margin): the first
        // push after the pin fills the channel, the second exhausts the pool —
        // acquire three times without release → third push must drop.
        using var blocker = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var delivery2 = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ =>
            {
                blocker.Set();
                release.Wait();
                return FrameSendResult.Sent;
            },
            capacity: 1);
        delivery2.Push(bitmap);
        Assert.IsTrue(blocker.Wait(TimeSpan.FromSeconds(5)), "Sender loop must pin the pooled buffer");

        // The pooled buffer is in flight; the margin buffer fills the channel.
        Assert.AreEqual(FrameDeliveryResult.Queued, delivery2.Push(bitmap), "The in-flight-margin buffer must queue (channel has room)");
        // Both buffers are now in flight — a third push has nothing to acquire.
        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery2.Push(bitmap));
        Assert.AreEqual(1, delivery2.DroppedCount);
        release.Set();
    }

    // ── coalescing: backlog drops stale frames ─────────────

    [TestMethod]
    public async Task Backlog_CoalescesToLatestFrame()
    {
        using var firstEntered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int callCount = 0;
        byte[]? lastDelivered = null;
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: bytes =>
            {
                lastDelivered = bytes;
                if (Interlocked.Increment(ref callCount) == 1)
                    firstEntered.Set();
                release.Wait();
                return FrameSendResult.Sent;
            },
            capacity: 4);

        // Red fills the channel first and pins the sender loop on the gate.
        using var redFrame = CreateFrame(SKColors.Red);
        delivery.Push(redFrame);
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(5)), "Sender loop must start delivering the first frame");

        // Backlog while the loop is blocked. Channel capacity 4 (DropOldest):
        // all three queue, and the coalescer discards the two stale ones.
        using var greenFrame = CreateFrame(SKColors.Green);
        using var blueFrame = CreateFrame(SKColors.Blue);
        using var whiteFrame = CreateFrame(SKColors.White);
        delivery.Push(greenFrame);
        delivery.Push(blueFrame);
        delivery.Push(whiteFrame);

        release.Set();

        // After the gate opens, the loop coalesces the backlog and delivers
        // only the latest (white). Wait for the second delivery.
        await TestWait.WaitUntilAsync(() => callCount >= 2, TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, callCount, "First frame + one coalesced delivery, no replays of stale frames");
        Assert.IsNotNull(lastDelivered);
        Assert.AreEqual(0xFF, lastDelivered[0], "Coalesced frame must be the latest (white) — byte 0");
        Assert.AreEqual(0xFF, lastDelivered[1], "Coalesced frame must be the latest (white) — byte 1");
        Assert.IsTrue(delivery.DroppedCount > 0, "Stale buffered frames must count as dropped");
        Assert.IsTrue(delivery.DroppedCoalescedCount > 0, "Stale frames are coalescer drops");
        Assert.AreEqual(0, delivery.DroppedPoolCount, "The pool held buffers — none of these drops were pool exhaustion");
    }

    // ── pacing: min interval between sends ─────────────────

    [TestMethod]
    public async Task Pacing_RespectsMinIntervalBetweenSends()
    {
        List<DateTime> timestamps = [];
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ =>
            {
                lock (timestamps) { timestamps.Add(DateTime.UtcNow); }
                return FrameSendResult.Sent;
            },
            minInterval: TimeSpan.FromMilliseconds(120));
        using var frame = CreateFrame(SKColors.White);

        delivery.Push(frame);
        delivery.Push(frame);
        delivery.Push(frame);

        await TestWait.WaitUntilAsync(() => timestamps.Count >= 3, TimeSpan.FromSeconds(5));

        Assert.AreEqual(3, timestamps.Count, "All three frames must eventually be delivered");
        lock (timestamps)
        {
            for (int i = 1; i < timestamps.Count; i++)
            {
                Assert.IsTrue(
                    (timestamps[i] - timestamps[i - 1]).TotalMilliseconds >= 100,
                    $"Send {i} must respect the min interval (gap was {(timestamps[i] - timestamps[i - 1]).TotalMilliseconds:F0}ms)");
            }
        }
    }

    // ── USB-mode readiness via predicate ───────────────────

    [TestMethod]
    public void UsbMode_IsReadyOnlyWhenPredicateAllows()
    {
        bool ready = true;
        int sent = 0;
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ =>
            {
                sent++;
                entered.Set();
                release.Wait();
                return FrameSendResult.Sent;
            },
            isReady: () => ready,
            capacity: 1);
        using var bitmap = CreateFrameBitmap();

        Assert.IsTrue(delivery.IsReady);
        Assert.AreEqual(FrameDeliveryResult.Queued, delivery.Push(bitmap), "Ready delivery must accept the frame");
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)), "Sender loop must deliver the ready frame");

        ready = false;
        Assert.IsFalse(delivery.IsReady);
        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery.Push(bitmap), "Not-ready delivery must drop the frame before encoding/queuing");
        release.Set();
        Assert.AreEqual(1, sent, "The dropped frame must never reach the transport seam");
    }

    // ── send-failure accounting: a broken pipe is not silence ──

    [TestMethod]
    public async Task SendFailure_CountsFailedAndNotSent()
    {
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ => FrameSendResult.Failed,
            capacity: 1);
        using var bitmap = CreateFrameBitmap();

        delivery.Push(bitmap);
        await TestWait.WaitUntilAsync(() => delivery.SendFailedCount > 0, TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, delivery.SendFailedCount);
        Assert.AreEqual(0, delivery.FramesSent, "A failed send is not a successful send");
        Assert.AreEqual(0, delivery.SendRefusedCount, "A broken pipe is not a device refusal");
        Assert.AreEqual(0, delivery.DroppedCount, "A send failure reached the transport seam — it is not a drop");
    }

    [TestMethod]
    public async Task SendRefusal_CountsRefusedNotFailedOrDropped()
    {
        // The tri-state seam: a device that declines the frame (not connected)
        // is provably distinct from a broken pipe (Failed) and from a drop —
        // the old bool seam folded all three into false.
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ => FrameSendResult.Refused,
            capacity: 1);
        using var bitmap = CreateFrameBitmap();

        delivery.Push(bitmap);
        await TestWait.WaitUntilAsync(() => delivery.SendRefusedCount > 0, TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, delivery.SendRefusedCount);
        Assert.AreEqual(0, delivery.SendFailedCount, "a refusal is not a failed transfer");
        Assert.AreEqual(0, delivery.FramesSent);
        Assert.AreEqual(0, delivery.DroppedCount, "a refusal reached the transport seam — it is not a drop");
    }

    [TestMethod]
    public async Task SendException_CountsAsSendFailed()
    {
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ => throw new InvalidOperationException("boom"),
            capacity: 1);
        using var bitmap = CreateFrameBitmap();

        delivery.Push(bitmap);
        await TestWait.WaitUntilAsync(() => delivery.SendFailedCount > 0, TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, delivery.SendFailedCount);
        Assert.AreEqual(0, delivery.FramesSent);
    }

    [TestMethod]
    public async Task SendFailure_LogsFirstFailureOnce()
    {
        List<string> logs = [];
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ => FrameSendResult.Failed,
            log: logs.Add,
            capacity: 1);
        using var bitmap = CreateFrameBitmap();

        delivery.Push(bitmap);
        await TestWait.WaitUntilAsync(() => delivery.SendFailedCount > 0, TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, logs.Count, "The first send failure must be logged exactly once");
        StringAssert.Contains(logs[0], "Send failed");
    }

    [TestMethod]
    public void PoolExhaustion_LogsFirstDropThroughTheLogSeam()
    {
        List<string> logs = [];
        using var blocker = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ =>
            {
                blocker.Set();
                release.Wait();
                return FrameSendResult.Sent;
            },
            log: logs.Add,
            capacity: 1);
        using var bitmap = CreateFrameBitmap();

        delivery.Push(bitmap);
        Assert.IsTrue(blocker.Wait(TimeSpan.FromSeconds(5)), "Sender loop must pin the pooled buffer");
        Assert.AreEqual(FrameDeliveryResult.Queued, delivery.Push(bitmap), "The in-flight-margin buffer must queue");
        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery.Push(bitmap));

        Assert.IsTrue(logs.Count > 0 && logs[0].Contains("dropped"),
            "the first pool-exhaustion drop must surface through the log seam — a wedged pipe that drops frames is visible, not silent");
        release.Set();
    }

    [TestMethod]
    public void PoolExhaustion_CountsAsPoolDropNotCoalescerDrop()
    {
        using var blocker = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            send: _ =>
            {
                blocker.Set();
                release.Wait();
                return FrameSendResult.Sent;
            },
            capacity: 1);
        using var bitmap = CreateFrameBitmap();

        delivery.Push(bitmap);
        Assert.IsTrue(blocker.Wait(TimeSpan.FromSeconds(5)), "Sender loop must pin the pooled buffer");

        // Pool of 2 (capacity 1 + the sender's in-flight margin): the first
        // push after the pin fills the channel, the second exhausts the pool.
        Assert.AreEqual(FrameDeliveryResult.Queued, delivery.Push(bitmap), "The in-flight-margin buffer must queue (channel has room)");
        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery.Push(bitmap));
        release.Set();

        Assert.AreEqual(1, delivery.DroppedPoolCount);
        Assert.AreEqual(0, delivery.DroppedCoalescedCount, "Pool exhaustion is not a coalescer drop");
    }

    // ── pool sizing: the pool is built from the encoder's output size ──

    [TestMethod]
    public void Push_WithCustomSizedEncoder_PoolMatchesEncoderOutputSize()
    {
        // A delivery on a non-standard encoder must pool buffers of exactly
        // that encoder's output size — the pool self-sizes from the seam, so
        // a "wrong-size" (relative to the display constant) send still flows.
        using var delivered = new ManualResetEventSlim(false);
        byte[]? received = null;
        using var delivery = FrameDelivery.Create(
            encoder: new FixedSizeEncoder(4096),
            send: bytes =>
            {
                received = bytes;
                delivered.Set();
                return FrameSendResult.Sent;
            });
        using var bitmap = CreateFrameBitmap();

        Assert.AreEqual(FrameDeliveryResult.Queued, delivery.Push(bitmap));
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "Sender loop must deliver the frame");
        Assert.IsNotNull(received);
        Assert.AreEqual(4096, received.Length, "The pool must be sized from the encoder's OutputBufferSize, not a fixed constant");
    }

    /// <summary>
    /// Test-only encoder with a caller-chosen output size; the delivery must
    /// pool buffers of exactly this size regardless of the display constant.
    /// </summary>
    private sealed class FixedSizeEncoder : IRgb565Encoder
    {
        private readonly int _outputSize;
        private bool _throwOnEncode;

        public FixedSizeEncoder(int outputSize) => _outputSize = outputSize;

        public void SetThrowOnEncode(bool value) => _throwOnEncode = value;

        public int OutputBufferSize => _outputSize;

        public void Encode(SKBitmap bitmap, byte[] destination)
        {
            if (_throwOnEncode) throw new InvalidOperationException("boom");
            destination[0] = 0xAB;
            destination[1] = 0xCD;
        }
    }

    private static SKBitmap CreateFrameBitmap()
    {
        var bitmap = new SKBitmap(
            DisplayProtocolConstants.FramebufferWidth,
            DisplayProtocolConstants.FramebufferHeight,
            SKColorType.Rgba8888,
            SKAlphaType.Opaque);
        bitmap.Erase(SKColors.DarkGray);
        return bitmap;
    }

    private static SKBitmap CreateFrame(SKColor color)
    {
        var bitmap = new SKBitmap(
            DisplayProtocolConstants.FramebufferWidth,
            DisplayProtocolConstants.FramebufferHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }
}
