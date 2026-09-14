using ModernWigiDash.Hardware.Service;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class HwinfoWidgetTests
{
    [TestCleanup]
    public void Cleanup() => VendorService.SetInstance(null);

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
        VendorService.SetInstance(CreateFakeClient(sensorsReady: false));
        var widget = new HwinfoWidget();
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        widget.Render(canvas, new SKRect(0, 0, 200, 100));
        Assert.AreEqual(100, bitmap.Height);
    }

    [TestMethod]
    public void Render_WithSensorData_ProducesCanvas()
    {
        VendorService.SetInstance(CreateFakeClient(sensorsReady: true, sensorName: "CPU Temp", sensorUnit: "°C", value: 45.5));
        // An empty pick falls back to the first sensor.
        var widget = new HwinfoWidget { SensorKey = "" };
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        widget.Render(canvas, new SKRect(0, 0, 200, 100));
        Assert.AreEqual(200, bitmap.Width);
    }

    [TestMethod]
    public void Render_UnknownSensorKey_ProducesCanvas()
    {
        VendorService.SetInstance(CreateFakeClient(sensorsReady: true, sensorCount: 1));
        var widget = new HwinfoWidget { SensorKey = "9:9:9" };
        using var bitmap = new SKBitmap(200, 100);
        using var canvas = new SKCanvas(bitmap);
        widget.Render(canvas, new SKRect(0, 0, 200, 100));
        Assert.AreEqual(100, bitmap.Height);
    }

    [TestMethod]
    public void GetPropertyOptions_NoService_ReturnsEmpty()
    {
        VendorService.SetInstance(null);
        var widget = new HwinfoWidget();
        Assert.AreEqual(0, widget.GetPropertyOptions(nameof(HwinfoWidget.SensorKey)).Count);
    }

    [TestMethod]
    public void GetPropertyOptions_ListsSensorKeysAndLabels()
    {
        VendorService.SetInstance(CreateFakeClient(sensorsReady: true, sensorName: "CPU Temp", sensorUnit: "°C", sensorCount: 2));
        var widget = new HwinfoWidget();

        var options = widget.GetPropertyOptions(nameof(HwinfoWidget.SensorKey));

        Assert.AreEqual(2, options.Count);
        Assert.AreEqual("0:0:0", options[0].Value);
        Assert.AreEqual("0:1:0", options[1].Value);
        Assert.AreEqual("CPU Temp", options[0].DisplayName);
    }

    [TestMethod]
    public void GetPropertyOptions_OtherProperty_ReturnsEmpty()
    {
        VendorService.SetInstance(CreateFakeClient(sensorsReady: true));
        var widget = new HwinfoWidget();
        Assert.AreEqual(0, widget.GetPropertyOptions(nameof(HwinfoWidget.Decimals)).Count);
    }

    [TestMethod]
    public void TryParseKey_RoundTripsComposeKey()
    {
        Assert.IsTrue(HwinfoWidget.TryParseKey(HwinfoWidget.ComposeKey(6, 12, 3), out int rt, out int id1, out int id2));
        Assert.AreEqual(6, rt);
        Assert.AreEqual(12, id1);
        Assert.AreEqual(3, id2);
    }

    [TestMethod]
    public void TryParseKey_EmptyOrGarbage_ReturnsFalse()
    {
        Assert.IsFalse(HwinfoWidget.TryParseKey("", out _, out _, out _));
        Assert.IsFalse(HwinfoWidget.TryParseKey("not-a-key", out _, out _, out _));
        Assert.IsFalse(HwinfoWidget.TryParseKey("1:2", out _, out _, out _));
    }

    [TestMethod]
    public void Render_EachDisplayMode_ComposesOutput()
    {
        foreach (var mode in new[] { "Gauge", "Bar", "Value", "Graph" })
        {
            VendorService.SetInstance(CreateFakeClient(sensorsReady: true, sensorName: "CPU Temp", sensorUnit: "°C", value: 45.5));
            var widget = new HwinfoWidget { DisplayMode = mode };
            using var surface = SKSurface.Create(new SKImageInfo(203, 148));

            // A few frames so the sparkline path (history >= 2) is exercised.
            for (int i = 0; i < 3; i++)
            {
                widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));
            }

            var pixel = surface.PeekPixels().GetPixelColor(101, 74);
            Assert.AreNotEqual(SKColors.Transparent, pixel, $"The {mode} mode must paint output");
        }
    }

    [TestMethod]
    public void Render_GraphMode_HistoryCapsAtCapacity()
    {
        VendorService.SetInstance(CreateFakeClient(sensorsReady: true, sensorName: "CPU Temp", sensorUnit: "°C", value: 45.5));
        var widget = new HwinfoWidget { DisplayMode = "Graph" };

        using var surface = SKSurface.Create(new SKImageInfo(203, 148));
        var bounds = new SKRect(0, 0, 203, 148);
        for (int i = 0; i < 150; i++)
        {
            widget.Render(surface.Canvas, bounds);
        }

        Assert.AreEqual(TelemetryReadingRenderer.HistoryCapacity, widget.HistoryCountForTest, "The history buffer must cap at its capacity");
    }

    private static WigiDashServiceClient CreateFakeClient(
        bool sensorsReady,
        string? sensorName = null,
        string? sensorUnit = null,
        double value = 0,
        int sensorCount = 1)
    {
        return new WigiDashServiceClient(new FakeSensorValues(sensorsReady, sensorName, sensorUnit, value, sensorCount));
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
}
