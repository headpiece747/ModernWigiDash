namespace ModernWigiDash.Tests;

/// <summary>
/// HardwareMonitorWidget render modes and gauge math, driven through the
/// LhmSensorStore with a fresh connected snapshot (the store's staleness
/// decision is the widget's only data gate).
/// </summary>
[TestClass]
public class HardwareMonitorWidgetTests
{
    private static readonly SensorReadingDto CpuTemp = new()
    {
        SensorId = "cpu-temp",
        SensorName = "CPU Package",
        HardwareName = "Mainboard",
        Unit = "°C",
        Value = 55.5,
        Min = 40,
        Max = 90,
    };

    private static HardwareMonitorWidget CreateWidget() => new()
    {
        SensorLabel = "Mainboard: CPU Package",
        DisplayLabel = "",
        Unit = ""
    };

    private static void SeedFreshSnapshot()
    {
        LhmSensorStore.UpdateFromDto(new SensorSnapshotDto
        {
            IsConnected = true,
            LastUpdate = DateTime.UtcNow,
            Readings = [CpuTemp],
        });
    }

    private static SKSurface CreateSurface() => SKSurface.Create(new SKImageInfo(203, 148));

    [TestInitialize]
    public void ResetStore() => LhmSensorStore.Reset();

    [TestMethod]
    public void Render_GaugeMode_WithConnectedSensor_ComposesOutput()
    {
        SeedFreshSnapshot();
        var widget = CreateWidget();
        widget.DisplayMode = "Gauge";

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        var pixel = surface.PeekPixels().GetPixelColor(101, 74);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The gauge must paint output");
    }

    [TestMethod]
    public void Render_BarMode_WithConnectedSensor_ComposesOutput()
    {
        SeedFreshSnapshot();
        var widget = CreateWidget();
        widget.DisplayMode = "Bar";

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        var pixel = surface.PeekPixels().GetPixelColor(101, 74);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The bar must paint output");
    }

    [TestMethod]
    public void Render_ValueMode_WithConnectedSensor_ComposesOutput()
    {
        SeedFreshSnapshot();
        var widget = CreateWidget();
        widget.DisplayMode = "Value";

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        var pixel = surface.PeekPixels().GetPixelColor(101, 74);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The value mode must paint output");
    }

    [TestMethod]
    public void Render_GraphMode_WithConnectedSensor_ComposesOutput()
    {
        SeedFreshSnapshot();
        var widget = CreateWidget();
        widget.DisplayMode = "Graph";

        using var surface = CreateSurface();
        // A few frames so the sparkline path (history >= 2) is exercised.
        for (int i = 0; i < 3; i++)
        {
            widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));
        }

        var pixel = surface.PeekPixels().GetPixelColor(101, 74);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The graph must paint output");
    }

    [TestMethod]
    public void Render_NoFreshSnapshot_ShowsUnavailablePlaceholder()
    {
        var widget = CreateWidget();

        using var surface = CreateSurface();
        // Store is reset in TestInitialize: no snapshot, no sensor data.
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        var pixel = surface.PeekPixels().GetPixelColor(101, 74);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The unavailable placeholder must still paint output");
    }

    [TestMethod]
    public void Render_UnknownSensorLabel_ShowsNotFoundPlaceholder()
    {
        SeedFreshSnapshot();
        var widget = new HardwareMonitorWidget { SensorLabel = "Mainboard: GPU Temp" };

        using var surface = CreateSurface();
        widget.Render(surface.Canvas, new SKRect(0, 0, 203, 148));

        var pixel = surface.PeekPixels().GetPixelColor(101, 74);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The sensor-not-found placeholder must still paint output");
    }

    [TestMethod]
    public void History_CappedAtCapacity_AfterManyRenders()
    {
        SeedFreshSnapshot();
        var widget = CreateWidget();
        widget.DisplayMode = "Graph";

        using var surface = CreateSurface();
        var bounds = new SKRect(0, 0, 203, 148);
        for (int i = 0; i < 150; i++)
        {
            widget.Render(surface.Canvas, bounds);
        }

        Assert.AreEqual(96, widget.HistoryCountForTest, "The history buffer must cap at its capacity");
    }
}
