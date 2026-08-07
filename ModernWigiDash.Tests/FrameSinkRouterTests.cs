using ModernWigiDash.App.FrameSinks;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class FrameSinkRouterTests
{
    [TestMethod]
    public void Send_WhenWcfReady_RoutesToWcf()
    {
        var wcf = new FakeSink { IsReady = true };
        var usb = new FakeSink { IsReady = true };
        using var router = new FrameSinkRouter(wcf, usb);
        using var frame = new SKBitmap(16, 16);

        bool result = router.Send(frame);

        Assert.IsTrue(result);
        Assert.AreEqual(1, wcf.SentFrames);
        Assert.AreEqual(0, usb.SentFrames);
    }

    [TestMethod]
    public void Send_WhenWcfNotReadyButUsbReady_RoutesToUsb()
    {
        var wcf = new FakeSink { IsReady = false };
        var usb = new FakeSink { IsReady = true };
        using var router = new FrameSinkRouter(wcf, usb);
        using var frame = new SKBitmap(16, 16);

        bool result = router.Send(frame);

        Assert.IsTrue(result);
        Assert.AreEqual(0, wcf.SentFrames);
        Assert.AreEqual(1, usb.SentFrames);
    }

    [TestMethod]
    public void Send_WhenNoSinkReady_ReturnsFalse()
    {
        var wcf = new FakeSink { IsReady = false };
        var usb = new FakeSink { IsReady = false };
        using var router = new FrameSinkRouter(wcf, usb);
        using var frame = new SKBitmap(16, 16);

        bool result = router.Send(frame);

        Assert.IsFalse(result);
        Assert.AreEqual(0, wcf.SentFrames);
        Assert.AreEqual(0, usb.SentFrames);
    }

    [TestMethod]
    public void Send_WhenNoSinkReadyAndHardwareActive_TriggersRetry()
    {
        var wcf = new FakeSink { IsReady = false };
        var usb = new FakeSink { IsReady = false };
        int retryCount = 0;
        using var router = new FrameSinkRouter(wcf, usb, retryTrigger: () => retryCount++, isHardwareActive: () => true);
        using var frame = new SKBitmap(16, 16);

        router.Send(frame);

        Assert.AreEqual(1, retryCount);
    }

    [TestMethod]
    public void Send_WhenWcfReady_DoesNotTriggerRetry()
    {
        var wcf = new FakeSink { IsReady = true };
        var usb = new FakeSink { IsReady = true };
        int retryCount = 0;
        using var router = new FrameSinkRouter(wcf, usb, retryTrigger: () => retryCount++, isHardwareActive: () => true);
        using var frame = new SKBitmap(16, 16);

        router.Send(frame);

        Assert.AreEqual(0, retryCount, "Retry must not fire while a sink can route");
    }

    [TestMethod]
    public void Send_WhenNoSinkReadyAndHardwareInactive_DoesNotTriggerRetry()
    {
        var wcf = new FakeSink { IsReady = false };
        var usb = new FakeSink { IsReady = false };
        int retryCount = 0;
        using var router = new FrameSinkRouter(wcf, usb, retryTrigger: () => retryCount++, isHardwareActive: () => false);
        using var frame = new SKBitmap(16, 16);

        router.Send(frame);

        Assert.AreEqual(0, retryCount, "Retry must not fire when no device yielded (sim mode / no device)");
    }

    [TestMethod]
    public void Dispose_DisposesBothSinks()
    {
        var wcf = new FakeSink { IsReady = false };
        var usb = new FakeSink { IsReady = false };
        var router = new FrameSinkRouter(wcf, usb);

        router.Dispose();

        Assert.IsTrue(wcf.Disposed);
        Assert.IsTrue(usb.Disposed);
    }

    private sealed class FakeSink : IFrameSink
    {
        public bool IsReady { get; set; }
        public int SentFrames { get; private set; }
        public bool Disposed { get; private set; }

        public bool SendFrame(SKBitmap frame)
        {
            SentFrames++;
            return IsReady;
        }

        public void Dispose() => Disposed = true;
    }
}
