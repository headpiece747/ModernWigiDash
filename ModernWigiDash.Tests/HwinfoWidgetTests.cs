using ModernWigiDash.Hardware.Service;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class HwinfoWidgetTests
{
    [TestMethod]
    public void Render_NoService_ProducesCanvas()
    {
        VendorService.SetInstance(null);
        var widget = new HwinfoWidget();
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        widget.Render(canvas, new SKRect(0, 0, 200, 100));
        Assert.AreEqual(200, bitmap.Width);
    }

    [TestMethod]
    public void Render_ServiceNotReady_ProducesCanvas()
    {
        var client = CreateFakeClient(sensorsReady: false);
        VendorService.SetInstance(client);
        try
        {
            var widget = new HwinfoWidget();
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
    public void Render_WithSensorData_ProducesCanvas()
    {
        var client = CreateFakeClient(sensorsReady: true, sensorName: "CPU Temp", sensorUnit: "°C", value: 45.5);
        VendorService.SetInstance(client);
        try
        {
            var widget = new HwinfoWidget { SensorIndex = 0 };
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
    public void Render_SensorIndexOutOfRange_ProducesCanvas()
    {
        var client = CreateFakeClient(sensorsReady: true, sensorCount: 1);
        VendorService.SetInstance(client);
        try
        {
            var widget = new HwinfoWidget { SensorIndex = 5 };
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

    private static WigiDashServiceClient CreateFakeClient(
        bool sensorsReady,
        string? sensorName = null,
        string? sensorUnit = null,
        double value = 0,
        int sensorCount = 1)
    {
        return new WigiDashServiceClient(new FakeSensorValues(sensorsReady, sensorName, sensorUnit, value, sensorCount), new FakeAidaPanel(false));
    }

    private sealed class FakeSensorValues : ISensorValues
    {
        private readonly bool _ready;
        private readonly string? _name;
        private readonly string? _unit;
        private readonly double _value;
        private readonly int _count;

        public FakeSensorValues(bool ready, string? name, string? unit, double value, int count)
        {
            _ready = ready;
            _name = name;
            _unit = unit;
            _value = value;
            _count = count;
        }

        public bool IsReady => _ready;

        public IReadOnlyList<VendorSensorItem>? GetSensorList()
        {
            if (!_ready) return null;
            return Enumerable.Range(0, _count).Select(i => new VendorSensorItem(Guid.NewGuid(), _name ?? $"Sensor{i}", 0, i, 0, null, _unit)).ToList();
        }

        public VendorSensorReading? GetSensorValue(int readingType, int sensorId1, int sensorId2)
        {
            if (!_ready) return null;
            return new VendorSensorReading(_value, true);
        }
    }

    private sealed class FakeAidaPanel : IAidaPanel
    {
        private readonly bool _ready;
        public FakeAidaPanel(bool ready) => _ready = ready;
        public bool IsReady => _ready;
        public byte[]? ReadAidaMmap(int offset, int length) => null;
    }
}
