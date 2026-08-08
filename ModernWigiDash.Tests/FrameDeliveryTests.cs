using System.Threading;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using SkiaSharp;

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
    public void SendFrame_WhenNoSendAttached_ReturnsDropped()
    {
        using var delivery = new FrameDelivery();
        using var bitmap = CreateFrameBitmap();

        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery.SendFrame(bitmap));
    }

    [TestMethod]
    public void IsReady_AfterAttachSend_IsTrue()
    {
        using var delivery = new FrameDelivery(send: _ => true);

        Assert.IsTrue(delivery.IsReady);
    }

    [TestMethod]
    public void IsReady_AfterAttachSendNull_IsFalse()
    {
        using var delivery = new FrameDelivery(send: _ => true);

        delivery.AttachSend(null);

        Assert.IsFalse(delivery.IsReady);
    }

    [TestMethod]
    public void IsReady_ReadinessPredicateOverridesDefault()
    {
        using var delivery = new FrameDelivery(send: _ => true, isReady: () => false);

        Assert.IsFalse(delivery.IsReady);
    }

    // ── Push (SKBitmap): encode → pooled buffer → deliver ──

    [TestMethod]
    public void Push_WithoutEncoder_ReturnsDropped()
    {
        using var delivery = new FrameDelivery(send: _ => true);
        using var bitmap = CreateFrameBitmap();

        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery.Push(bitmap));
    }

    [TestMethod]
    public void Push_WithEncoderAndSend_DeliversEncodedRgb565Frame()
    {
        using var delivered = new ManualResetEventSlim(false);
        byte[]? received = null;
        var pool = new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4);
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            pool: pool,
            send: bytes =>
            {
                received = bytes;
                delivered.Set();
                return true;
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
        var pool = new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 1);
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            pool: pool,
            send: _ =>
            {
                delivered.Set();
                return true;
            });
        using var bitmap = CreateFrameBitmap();

        delivery.Push(bitmap);
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "Sender loop must deliver the frame");

        byte[]? buffer = null;
        await TestWait.WaitUntilAsync(() =>
        {
            buffer = pool.Acquire();
            return buffer != null;
        }, TimeSpan.FromSeconds(2));
        Assert.IsNotNull(buffer, "Sender loop must release the buffer back to the pool after delivery");
        pool.Release(buffer);
    }

    [TestMethod]
    public void Push_WhenPoolExhausted_ReturnsDroppedAndCounts()
    {
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            pool: new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 1),
            send: _ => false);
        using var bitmap = CreateFrameBitmap();

        // First push: buffer acquired, send fails, buffer released back.
        delivery.Push(bitmap);
        // Second push: the loop may have released the buffer already; force the
        // no-buffer path by exhausting with a blocking send.
        Assert.AreEqual(0, delivery.DroppedCount);

        // Pool of 1: acquire twice without release → second push must drop.
        using var blocker = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        using var delivery2 = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            pool: new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 1),
            send: _ =>
            {
                blocker.Set();
                release.Wait();
                return true;
            });
        delivery2.Push(bitmap);
        Assert.IsTrue(blocker.Wait(TimeSpan.FromSeconds(5)), "Sender loop must pin the pooled buffer");

        // The pooled buffer is in flight; a second push has no buffer to acquire.
        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery2.Push(bitmap));
        Assert.AreEqual(1, delivery2.DroppedCount);
        release.Set();
    }

    // ── PushBytes: byte-level entry (service hop) ──────────

    [TestMethod]
    public void PushBytes_EmptyFrame_ReturnsDropped()
    {
        using var delivery = new FrameDelivery(send: _ => true);

        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery.PushBytes([]));
        Assert.AreEqual(FrameDeliveryResult.Dropped, delivery.PushBytes(null!));
    }

    [TestMethod]
    public void PushBytes_WithSend_Delivers()
    {
        using var delivered = new ManualResetEventSlim(false);
        byte[]? received = null;
        using var delivery = new FrameDelivery(send: bytes =>
        {
            received = bytes;
            delivered.Set();
            return true;
        });

        byte[] frame = new byte[64];
        Assert.AreEqual(FrameDeliveryResult.Queued, delivery.PushBytes(frame));
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)));
        Assert.AreSame(frame, received, "Byte-level push must not copy — the array is owned by the pipeline");
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
            pool: new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4),
            send: bytes =>
            {
                lastDelivered = bytes;
                if (Interlocked.Increment(ref callCount) == 1)
                    firstEntered.Set();
                release.Wait();
                return true;
            });

        // Red fills the channel first and pins the sender loop on the gate.
        using var redFrame = CreateFrame(SKColors.Red);
        delivery.Push(redFrame);
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(5)), "Sender loop must start delivering the first frame");

        // Backlog while the loop is blocked. Capacity 2 (DropOldest): green
        // drops out when white arrives, leaving blue + white queued.
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
    }

    // ── pacing: min interval between sends ─────────────────

    [TestMethod]
    public async Task Pacing_RespectsMinIntervalBetweenSends()
    {
        var timestamps = new List<DateTime>();
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            pool: new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4),
            send: _ =>
            {
                lock (timestamps) { timestamps.Add(DateTime.UtcNow); }
                return true;
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
        using var delivery = new FrameDelivery(
            encoder: new SkiaRgb565Encoder(),
            pool: new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 1),
            send: _ =>
            {
                sent++;
                return true;
            },
            isReady: () => ready);

        Assert.IsTrue(delivery.IsReady);
        ready = false;
        Assert.IsFalse(delivery.IsReady);
        _ = sent; // sends are exercised by Push tests; readiness is the contract here
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
