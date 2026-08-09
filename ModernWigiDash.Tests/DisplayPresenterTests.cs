using ModernWigiDash.App;
using ModernWigiDash.Hardware.Transport;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class DisplayPresenterTests
{
    private static SKBitmap CreateFrame() => new(
        DisplayProtocolConstants.FramebufferWidth,
        DisplayProtocolConstants.FramebufferHeight,
        SKColorType.Bgra8888,
        SKAlphaType.Premul);

    [TestMethod]
    public void Send_WhenReady_DeliversFrameToTransport()
    {
        using var delivered = new ManualResetEventSlim(false);
        int sent = 0;
        using var presenter = new DisplayPresenter(
            send: bytes =>
            {
                sent++;
                delivered.Set();
                return true;
            },
            isReady: () => true);
        using var frame = CreateFrame();

        presenter.Send(frame);

        Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(5)), "Ready presenter must deliver the frame to the transport");
        Assert.AreEqual(1, sent);
    }

    [TestMethod]
    public void Send_WhenNotReady_DropsWithoutCallingTransport()
    {
        int sent = 0;
        using var presenter = new DisplayPresenter(
            send: bytes =>
            {
                sent++;
                return true;
            },
            isReady: () => false);
        using var frame = CreateFrame();

        presenter.Send(frame);

        Assert.AreEqual(0, sent, "Not-ready presenter must never call the transport");
        Assert.AreEqual(0, presenter.FramesSent);
        Assert.IsFalse(presenter.IsReady);
    }

    [TestMethod]
    public async Task FramesSent_CountsDeliveredFrames()
    {
        using var presenter = new DisplayPresenter(
            send: _ => true,
            isReady: () => true);
        using var frame = CreateFrame();

        presenter.Send(frame);

        await TestWait.WaitUntilAsync(() => presenter.FramesSent > 0, TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, presenter.FramesSent);
    }

    [TestMethod]
    public void Dispose_StopsDeliveryAndIsSafeToRepeat()
    {
        int sent = 0;
        using var presenter = new DisplayPresenter(
            send: bytes =>
            {
                sent++;
                return true;
            },
            isReady: () => true);

        presenter.Dispose();
        presenter.Dispose(); // idempotent — must not throw

        using var frame = CreateFrame();
        presenter.Send(frame); // dead pipeline — must drop before encoding/queuing

        Assert.AreEqual(0, sent, "Nothing may reach the transport after dispose");
        Assert.AreEqual(0, presenter.FramesSent);
    }
}
