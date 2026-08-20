using ModernWigiDash.App;
using ModernWigiDash.Sdk;
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
                return FrameSendResult.Sent;
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
                return FrameSendResult.Sent;
            },
            isReady: () => false);
        using var frame = CreateFrame();

        presenter.Send(frame);

        Assert.AreEqual(0, sent, "Not-ready presenter must never call the transport");
    }

    [TestMethod]
    public async Task Send_DeliversFrameToTransport()
    {
        int sent = 0;
        using var presenter = new DisplayPresenter(
            send: bytes =>
            {
                sent++;
                return FrameSendResult.Sent;
            },
            isReady: () => true);
        using var frame = CreateFrame();

        presenter.Send(frame);

        await TestWait.WaitUntilAsync(() => sent > 0, TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, sent);
    }

    [TestMethod]
    public void Dispose_StopsDeliveryAndIsSafeToRepeat()
    {
        int sent = 0;
        using var presenter = new DisplayPresenter(
            send: bytes =>
            {
                sent++;
                return FrameSendResult.Sent;
            },
            isReady: () => true);

        presenter.Dispose();
        presenter.Dispose(); // idempotent — must not throw

        using var frame = CreateFrame();
        presenter.Send(frame); // dead pipeline — must drop before encoding/queuing

        Assert.AreEqual(0, sent, "Nothing may reach the transport after dispose");
    }

    [TestMethod]
    public async Task Refusal_LogsThroughTheInjectedLogSeam()
    {
        List<string> logs = [];
        using var presenter = new DisplayPresenter(
            send: _ => FrameSendResult.Refused,
            isReady: () => true,
            log: logs.Add);
        using var frame = CreateFrame();

        presenter.Send(frame);

        await TestWait.WaitUntilAsync(() => logs.Count > 0, TimeSpan.FromSeconds(5));
        Assert.IsTrue(logs[0].Contains("refused"),
            "the refusal verdict must surface through the presenter's log seam — the verdict is observable end-to-end");
    }
}
