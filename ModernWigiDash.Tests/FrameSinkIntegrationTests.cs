using System.Threading;
using ModernWigiDash.App.FrameSinks;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// Composes the real <see cref="FrameSinkRouter"/>, <see cref="WcfFrameSink"/>
/// and <see cref="DirectUsbFrameSink"/> together to prove the App's frame-routing
/// wiring works end to end: WCF-priority routing, runtime readiness flips via
/// SetSend, the retry trigger, and backlog coalescing to the latest frame.
/// </summary>
[TestClass]
public class FrameSinkIntegrationTests
{
    [TestMethod]
    public void Router_WithRealSinks_RoutesToUsbWhileWcfUnbound_ThenSwitchesToWcfWhenBound()
    {
        using var delivered = new ManualResetEventSlim(false);
        var usbDevice = new FakeFrameSendDevice { IsConnected = true, IsSimulationMode = false };
        using var wcfSink = new WcfFrameSink(new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4));
        using var usbSink = new DirectUsbFrameSink(usbDevice);
        using var router = new FrameSinkRouter(wcfSink, usbSink);
        using var redFrame = CreateFrame(SKColors.Red);

        // WCF not yet bound -> direct USB must receive the frame.
        bool firstResult = router.Send(redFrame);
        Assert.IsTrue(firstResult, "Ready USB sink must accept the frame");
        Assert.AreEqual(1, usbDevice.SentFrames);

        // Bind the WCF path (simulates the service connecting) -> routing flips.
        wcfSink.SetSend(bytes =>
        {
            delivered.Set();
            return true;
        });
        bool secondResult = router.Send(redFrame);

        Assert.IsTrue(secondResult);
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "WCF sink must deliver after binding");
        Assert.AreEqual(1, usbDevice.SentFrames, "Routing must not fall back to USB while WCF is bound");
    }

    [TestMethod]
    public void Router_WithRealSinks_WcfUnbind_FallsBackToUsb()
    {
        var usbDevice = new FakeFrameSendDevice { IsConnected = true, IsSimulationMode = false };
        using var wcfSink = new WcfFrameSink(new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4));
        using var usbSink = new DirectUsbFrameSink(usbDevice);
        using var router = new FrameSinkRouter(wcfSink, usbSink);
        using var redFrame = CreateFrame(SKColors.Red);
        wcfSink.SetSend(_ => true);

        // Drain the WCF delivery, then unbind (simulates the service faulting).
        wcfSink.SetSend(null);
        bool result = router.Send(redFrame);

        Assert.IsTrue(result);
        Assert.AreEqual(1, usbDevice.SentFrames, "USB must take over once WCF is unbound");
    }

    [TestMethod]
    public void Router_WithRealSinks_NeitherReadyAndHardwareActive_TriggersRetry()
    {
        using var wcfSink = new WcfFrameSink(new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4));
        using var usbSink = new DirectUsbFrameSink(new FakeFrameSendDevice { IsConnected = false, IsSimulationMode = false });
        int retryCount = 0;
        using var router = new FrameSinkRouter(wcfSink, usbSink, retryTrigger: () => retryCount++, isHardwareActive: () => true);
        using var redFrame = CreateFrame(SKColors.Red);

        bool result = router.Send(redFrame);

        Assert.IsFalse(result);
        Assert.AreEqual(1, retryCount, "Engine yielded to a service -> retry detection must fire");
    }

    [TestMethod]
    public void Router_WithRealSinks_WcfBoundAndUsbReady_DoesNotTriggerRetry()
    {
        var usbDevice = new FakeFrameSendDevice { IsConnected = true, IsSimulationMode = false };
        using var wcfSink = new WcfFrameSink(new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4));
        using var usbSink = new DirectUsbFrameSink(usbDevice);
        int retryCount = 0;
        using var router = new FrameSinkRouter(wcfSink, usbSink, retryTrigger: () => retryCount++, isHardwareActive: () => true);
        using var redFrame = CreateFrame(SKColors.Red);
        wcfSink.SetSend(_ => true);

        router.Send(redFrame);

        Assert.AreEqual(0, retryCount, "Retry must not fire while a sink can route");
    }

    [TestMethod]
    public void WcfFrameSink_Backlog_CoalescesToLatestFrame()
    {
        using var firstEntered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        int callCount = 0;
        byte[]? lastDelivered = null;
        var pool = new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4);
        using var sink = new WcfFrameSink(pool);
        sink.SetSend(bytes =>
        {
            lastDelivered = bytes;
            if (Interlocked.Increment(ref callCount) == 1)
                firstEntered.Set();
            release.Wait();
            return true;
        });

        // Red fills the channel first and pins the sender loop on the gate.
        using var redFrame = CreateFrame(SKColors.Red);
        sink.SendFrame(redFrame);
        Assert.IsTrue(firstEntered.Wait(TimeSpan.FromSeconds(5)), "Sender loop must start delivering the first frame");

        // Backlog while the loop is blocked. Channel capacity is 2 (DropOldest):
        // green drops out when white arrives, leaving blue + white queued.
        using var greenFrame = CreateFrame(SKColors.Green);
        using var blueFrame = CreateFrame(SKColors.Blue);
        using var whiteFrame = CreateFrame(SKColors.White);
        sink.SendFrame(greenFrame);
        sink.SendFrame(blueFrame);
        sink.SendFrame(whiteFrame);

        release.Set();

        // After the gate opens, the loop coalesces the backlog and delivers only
        // the latest (white). Green's buffer was dropped, blue released in favor
        // of white. Wait for the second delivery.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (callCount < 2 && DateTime.UtcNow < deadline)
            Thread.Sleep(5);

        Assert.AreEqual(2, callCount, "First frame + one coalesced delivery, no replays of stale frames");
        Assert.IsNotNull(lastDelivered);
        Assert.AreEqual(0xFF, lastDelivered![0], "Coalesced frame must be the latest (white) — byte 0");
        Assert.AreEqual(0xFF, lastDelivered![1], "Coalesced frame must be the latest (white) — byte 1");
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

    private sealed class FakeFrameSendDevice : IFrameSendDevice
    {
        public bool IsConnected { get; set; }
        public bool IsSimulationMode { get; set; }
        public int SentFrames { get; private set; }

        public bool SendFrameBuffer(SKBitmap frame)
        {
            if (!IsConnected || IsSimulationMode)
                return false;
            SentFrames++;
            return true;
        }
    }
}
