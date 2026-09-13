using ModernWigiDash.Hardware.Service;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class AidaPanelWidgetTests
{
    [TestMethod]
    public void Render_NoService_ProducesCanvas()
    {
        VendorService.SetInstance(null);
        var widget = new AidaPanelWidget();
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        widget.Render(canvas, new SKRect(0, 0, 200, 100));
        Assert.AreEqual(200, bitmap.Width);
    }

    [TestMethod]
    public void Render_ServiceNotReady_ProducesCanvas()
    {
        var client = CreateFakeClient(aidaReady: false);
        VendorService.SetInstance(client);
        try
        {
            var widget = new AidaPanelWidget();
            using var bitmap = new SKBitmap(200, 100);
            using var canvas = new SKCanvas(bitmap);
            widget.Render(canvas, new SKRect(0, 0, 200, 100));
            Assert.AreEqual(100, bitmap.Height);
        }
        finally
        {
            VendorService.SetInstance(null);
        }
    }

    [TestMethod]
    public void Render_WithFrameData_ProducesCanvas()
    {
        var frameBytes = new byte[1016 * 592 * 2];
        for (int i = 0; i < frameBytes.Length; i += 2)
        {
            frameBytes[i] = 0xFF;
            frameBytes[i + 1] = 0x7F;
        }

        var client = CreateFakeClient(aidaReady: true, frameBuffer: frameBytes);
        VendorService.SetInstance(client);
        try
        {
            var widget = new AidaPanelWidget();
            using var bitmap = new SKBitmap(200, 100);
            using var canvas = new SKCanvas(bitmap);
            widget.Render(canvas, new SKRect(0, 0, 200, 100));
            Assert.AreEqual(200, bitmap.Width);
        }
        finally
        {
            VendorService.SetInstance(null);
        }
    }

    [TestMethod]
    public void Render_ShortBuffer_ProducesCanvas()
    {
        var shortBuffer = new byte[100];
        var client = CreateFakeClient(aidaReady: true, frameBuffer: shortBuffer);
        VendorService.SetInstance(client);
        try
        {
            var widget = new AidaPanelWidget();
            using var bitmap = new SKBitmap(200, 100);
            using var canvas = new SKCanvas(bitmap);
            widget.Render(canvas, new SKRect(0, 0, 200, 100));
            Assert.AreEqual(100, bitmap.Height);
        }
        finally
        {
            VendorService.SetInstance(null);
        }
    }

    private static WigiDashServiceClient CreateFakeClient(bool aidaReady, byte[]? frameBuffer = null)
    {
        return new WigiDashServiceClient(new FakeSensorValues(), new FakeAidaPanel(aidaReady, frameBuffer));
    }

    private sealed class FakeSensorValues : ISensorValues
    {
        public bool IsReady => false;
        public IReadOnlyList<VendorSensorItem>? GetSensorList() => null;
        public VendorSensorReading? GetSensorValue(int readingType, int sensorId1, int sensorId2) => null;
    }

    private sealed class FakeAidaPanel : IAidaPanel
    {
        private readonly bool _ready;
        private readonly byte[]? _buffer;

        public FakeAidaPanel(bool ready, byte[]? buffer)
        {
            _ready = ready;
            _buffer = buffer;
        }

        public bool IsReady => _ready;
        public byte[]? ReadAidaMmap(int offset, int length) => _buffer;
    }
}
