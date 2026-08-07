using System.Threading;
using ModernWigiDash.App.FrameSinks;
using ModernWigiDash.Hardware.Transport;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class WcfFrameSinkTests
{
    [TestMethod]
    public void IsReady_WhenNoSendBound_IsFalse()
    {
        using var sink = new WcfFrameSink();

        Assert.IsFalse(sink.IsReady);
    }

    [TestMethod]
    public void SendFrame_WhenNoSendBound_ReturnsFalse()
    {
        using var sink = new WcfFrameSink();
        using var bitmap = CreateFrameBitmap();

        Assert.IsFalse(sink.SendFrame(bitmap));
    }

    [TestMethod]
    public void IsReady_AfterSetSend_IsTrue()
    {
        using var sink = new WcfFrameSink();

        sink.SetSend(_ => true);

        Assert.IsTrue(sink.IsReady);
    }

    [TestMethod]
    public void IsReady_AfterSetSendNull_IsFalse()
    {
        using var sink = new WcfFrameSink();
        sink.SetSend(_ => true);

        sink.SetSend(null);

        Assert.IsFalse(sink.IsReady);
    }

    [TestMethod]
    public void SendFrame_WithSendBound_DeliversEncodedRgb565Frame()
    {
        using var delivered = new ManualResetEventSlim(false);
        byte[]? received = null;
        var pool = new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 4);
        using var sink = new WcfFrameSink(pool);
        sink.SetSend(bytes =>
        {
            received = bytes;
            delivered.Set();
            return true;
        });
        using var bitmap = CreateFrameBitmap();

        bool accepted = sink.SendFrame(bitmap);

        Assert.IsTrue(accepted, "Ready sink must accept the frame");
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "Sender loop must deliver the frame");
        Assert.IsNotNull(received);
        Assert.AreEqual(DisplayProtocolConstants.FrameBufferSize, received!.Length);
    }

    [TestMethod]
    public void SendFrame_AfterDelivery_ReturnsBufferToPool()
    {
        using var delivered = new ManualResetEventSlim(false);
        var pool = new FrameBufferPool(DisplayProtocolConstants.FrameBufferSize, capacity: 1);
        using var sink = new WcfFrameSink(pool);
        sink.SetSend(_ =>
        {
            delivered.Set();
            return true;
        });
        using var bitmap = CreateFrameBitmap();

        sink.SendFrame(bitmap);
        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "Sender loop must deliver the frame");

        byte[]? buffer = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (buffer == null && DateTime.UtcNow < deadline)
        {
            buffer = pool.Acquire();
            if (buffer == null) Thread.Sleep(5);
        }
        Assert.IsNotNull(buffer, "Sender loop must release the buffer back to the pool after delivery");
        pool.Release(buffer!);
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
}
