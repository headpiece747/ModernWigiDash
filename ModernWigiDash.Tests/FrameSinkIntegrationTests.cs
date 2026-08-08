using System.Threading;
using ModernWigiDash.App.FrameSinks;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// Composes the real <see cref="FrameSinkRouter"/> with two real
/// <see cref="FrameDelivery"/> instances (WCF-bound and USB-bound) to prove the
/// App's frame-routing wiring works end to end: WCF-priority routing, runtime
/// readiness flips via AttachSend, the retry trigger, and backlog coalescing
/// to the latest frame.
/// </summary>
[TestClass]
public class FrameSinkIntegrationTests
{
    private sealed class UsbFake
    {
        public bool Ready { get; set; } = true;
        public int SentFrames { get; private set; }

        public bool Send(byte[] bytes)
        {
            if (!Ready) return false;
            SentFrames++;
            _ = bytes.Length;
            return true;
        }
    }

    private static FrameDelivery CreateBitmapDelivery(Func<byte[], bool>? send = null, Func<bool>? isReady = null)
        => new(
            encoder: new SkiaRgb565Encoder(),
            pool: new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4),
            send: send,
            isReady: isReady);

    private static async Task WaitForCountAsync(Func<int> count, int expected, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (count() < expected && DateTime.UtcNow < deadline)
            await Task.Delay(5);
        Assert.AreEqual(expected, count(), what);
    }

    [TestMethod]
    public async Task Router_WithRealDeliveries_RoutesToUsbWhileWcfUnbound_ThenSwitchesToWcfWhenBound()
    {
        using var delivered = new ManualResetEventSlim(false);
        var usb = new UsbFake();
        using var wcfDelivery = CreateBitmapDelivery();
        using var usbDelivery = CreateBitmapDelivery(usb.Send, () => usb.Ready);
        using var router = new FrameSinkRouter(wcfDelivery, usbDelivery);
        using var redFrame = CreateFrame(SKColors.Red);

        // WCF not yet bound -> direct USB must receive the frame.
        FrameDeliveryResult firstResult = router.Send(redFrame);
        Assert.AreEqual(FrameDeliveryResult.Queued, firstResult, "Ready USB delivery must accept the frame");
        await WaitForCountAsync(() => usb.SentFrames, 1, "USB delivery must deliver after routing");

        // Bind the WCF path (simulates the service connecting) -> routing flips.
        wcfDelivery.AttachSend(bytes =>
        {
            delivered.Set();
            return true;
        });
        FrameDeliveryResult secondResult = router.Send(redFrame);

        Assert.AreEqual(FrameDeliveryResult.Queued, secondResult);
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "WCF delivery must deliver after binding");
        Assert.AreEqual(1, usb.SentFrames, "Routing must not fall back to USB while WCF is bound");
    }

    [TestMethod]
    public async Task Router_WithRealDeliveries_WcfUnbind_FallsBackToUsb()
    {
        var usb = new UsbFake();
        using var wcfDelivery = CreateBitmapDelivery(_ => true);
        using var usbDelivery = CreateBitmapDelivery(usb.Send, () => usb.Ready);
        using var router = new FrameSinkRouter(wcfDelivery, usbDelivery);
        using var redFrame = CreateFrame(SKColors.Red);

        // Unbind (simulates the service faulting).
        wcfDelivery.AttachSend(null);
        FrameDeliveryResult result = router.Send(redFrame);

        Assert.AreEqual(FrameDeliveryResult.Queued, result);
        await WaitForCountAsync(() => usb.SentFrames, 1, "USB must take over once WCF is unbound");
    }

    [TestMethod]
    public void Router_WithRealDeliveries_NeitherReadyAndHardwareActive_TriggersRetry()
    {
        using var wcfDelivery = CreateBitmapDelivery();
        var usb = new UsbFake { Ready = false };
        using var usbDelivery = CreateBitmapDelivery(usb.Send, () => usb.Ready);
        int retryCount = 0;
        using var router = new FrameSinkRouter(wcfDelivery, usbDelivery, retryTrigger: () => retryCount++, isHardwareActive: () => true);
        using var redFrame = CreateFrame(SKColors.Red);

        FrameDeliveryResult result = router.Send(redFrame);

        Assert.AreEqual(FrameDeliveryResult.Dropped, result);
        Assert.AreEqual(1, retryCount, "Engine yielded to a service -> retry detection must fire");
    }

    [TestMethod]
    public void Router_WithRealDeliveries_WcfBoundAndUsbReady_DoesNotTriggerRetry()
    {
        var usb = new UsbFake();
        using var wcfDelivery = CreateBitmapDelivery(_ => true);
        using var usbDelivery = CreateBitmapDelivery(usb.Send, () => usb.Ready);
        int retryCount = 0;
        using var router = new FrameSinkRouter(wcfDelivery, usbDelivery, retryTrigger: () => retryCount++, isHardwareActive: () => true);
        using var redFrame = CreateFrame(SKColors.Red);

        router.Send(redFrame);

        Assert.AreEqual(0, retryCount, "Retry must not fire while a delivery can route");
    }

    [TestMethod]
    public async Task FrameDelivery_Backlog_CoalescesToLatestFrame()
    {
        using var firstEntered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int callCount = 0;
        byte[]? lastDelivered = null;
        using var delivery = CreateBitmapDelivery(bytes =>
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

        // Backlog while the loop is blocked. Channel capacity is 2 (DropOldest):
        // green drops out when white arrives, leaving blue + white queued.
        using var greenFrame = CreateFrame(SKColors.Green);
        using var blueFrame = CreateFrame(SKColors.Blue);
        using var whiteFrame = CreateFrame(SKColors.White);
        delivery.Push(greenFrame);
        delivery.Push(blueFrame);
        delivery.Push(whiteFrame);

        release.Set();

        // After the gate opens, the loop coalesces the backlog and delivers only
        // the latest (white). Wait for the second delivery.
        await TestWait.WaitUntilAsync(() => callCount >= 2, TimeSpan.FromSeconds(5));

        Assert.AreEqual(2, callCount, "First frame + one coalesced delivery, no replays of stale frames");
        Assert.IsNotNull(lastDelivered);
        Assert.AreEqual(0xFF, lastDelivered[0], "Coalesced frame must be the latest (white) — byte 0");
        Assert.AreEqual(0xFF, lastDelivered[1], "Coalesced frame must be the latest (white) — byte 1");
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
