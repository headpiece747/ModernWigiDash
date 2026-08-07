using ModernWigiDash.App.FrameSinks;
using ModernWigiDash.Hardware.Transport;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class DirectUsbFrameSinkTests
{
    [TestMethod]
    public void IsReady_WhenConnectedAndNotSimulation_IsTrue()
    {
        var device = new FakeFrameSendDevice { IsConnected = true, IsSimulationMode = false };
        using var sink = new DirectUsbFrameSink(device);

        Assert.IsTrue(sink.IsReady);
    }

    [TestMethod]
    public void IsReady_WhenNotConnected_IsFalse()
    {
        var device = new FakeFrameSendDevice { IsConnected = false, IsSimulationMode = false };
        using var sink = new DirectUsbFrameSink(device);

        Assert.IsFalse(sink.IsReady);
    }

    [TestMethod]
    public void IsReady_WhenSimulationMode_IsFalse()
    {
        var device = new FakeFrameSendDevice { IsConnected = true, IsSimulationMode = true };
        using var sink = new DirectUsbFrameSink(device);

        Assert.IsFalse(sink.IsReady);
    }

    [TestMethod]
    public void SendFrame_WhenReady_DelegatesToDevice()
    {
        var device = new FakeFrameSendDevice { IsConnected = true, IsSimulationMode = false };
        using var sink = new DirectUsbFrameSink(device);
        using var frame = new SKBitmap(1016, 592);

        bool result = sink.SendFrame(frame);

        Assert.IsTrue(result);
        Assert.AreEqual(1, device.SentFrames);
    }

    [TestMethod]
    public void SendFrame_WhenNotConnected_ReturnsFalse()
    {
        var device = new FakeFrameSendDevice { IsConnected = false, IsSimulationMode = false };
        using var sink = new DirectUsbFrameSink(device);
        using var frame = new SKBitmap(1016, 592);

        bool result = sink.SendFrame(frame);

        Assert.IsFalse(result);
        Assert.AreEqual(0, device.SentFrames);
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
