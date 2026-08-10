using System.Net;
using System.Net.Http;
using ModernWigiDash.Widgets;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class WeatherForecastWidgetTests
{
    private const string SampleForecast = """
    {
      "latitude": 40.7128, "longitude": -74.006,
      "current_weather": { "temperature": 12.5, "windspeed": 8.2, "weathercode": 2, "time": "2026-08-07T12:00" },
      "hourly": {
        "time": ["2026-08-07T12:00", "2026-08-07T13:00"],
        "temperature_2m": [12.5, 13.1],
        "relativehumidity_2m": [60, 58],
        "weathercode": [2, 2]
      },
      "daily": {
        "time": ["2026-08-07", "2026-08-08"],
        "weathercode": [2, 3],
        "temperature_2m_max": [18.0, 20.0],
        "temperature_2m_min": [9.0, 11.0]
      }
    }
    """;


    private const string SampleGeocode = """
    {
      "results": [ { "name": "New York", "latitude": 40.7128, "longitude": -74.006, "country": "US" } ]
    }
    """;

    [TestMethod]
    public async Task FetchLiveWeather_Forecast_WithStubClient_ParsesIntoForecasts()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("/v1/search", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(SampleGeocode)
                : StubHttpHandler.Ok(SampleForecast);
        });
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(stub) };

        await widget.FetchLiveWeatherAsync(force: true);

        Assert.IsTrue(stub.Calls >= 2, "The fetch must hit the stub client (geocode + forecast)");
        Assert.IsTrue(widget._dailyForecasts.Count >= 2, "Daily forecast items must be parsed");
        Assert.IsTrue(widget._hourlyForecasts.Count >= 2, "Hourly forecast items must be parsed");
    }

    [TestMethod]
    public async Task FetchLiveWeather_Throttle_UsesInjectedClock()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("/v1/search", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(SampleGeocode)
                : StubHttpHandler.Ok(SampleForecast);
        });
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(stub), Clock = clock };

        await widget.FetchLiveWeatherAsync(force: true);
        int afterFirst = stub.Calls;

        // Within the 5-minute window the non-forced fetch must be throttled away.
        await widget.FetchLiveWeatherAsync();
        Assert.AreEqual(afterFirst, stub.Calls, "The 5-minute throttle must suppress a second fetch");

        clock.Advance(TimeSpan.FromMinutes(6));
        await widget.FetchLiveWeatherAsync();
        Assert.IsTrue(stub.Calls > afterFirst, "After the throttle window elapses, fetching resumes");
    }

    [TestMethod]
    public void Render_WithWiredAccent_ComposesWithoutThrowing()
    {
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var widget = new WeatherForecastWidget { AccentColorHex = "#FF0000", Location = "New York" };

        // Render must not throw with the wired accent color and invariant formatting,
        // and it must paint something (the widget draws its background).
        widget.Render(surface.Canvas, new SKRect(0, 0, 406, 296));
        var pixel = surface.PeekPixels().GetPixelColor(200, 148);
        Assert.AreNotEqual(SKColors.Transparent, pixel, "The composed surface must contain output");
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeClock(DateTimeOffset start) => _now = start;
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [TestMethod]
    public void WeatherForecastWidget_DefaultsAndProperties_InitializeCorrectly()
    {
        var widget = new WeatherForecastWidget();

        Assert.AreEqual("New York", widget.Location);
        Assert.AreEqual("Fixed Location", widget.LocationType);
        Assert.AreEqual("Detailed", widget.LayoutMode);
        Assert.AreEqual("Fahrenheit (°F, mph)", widget.UnitSystem);
        Assert.AreEqual("#F59E0B", widget.AccentColorHex);
        Assert.IsTrue(widget.ShowHumidity);
        Assert.IsTrue(widget.ShowWind);
        Assert.IsTrue(widget.ShowFeelsLike);
        Assert.IsTrue(widget.ShowHighLow);
        Assert.IsFalse(widget.StaticSnapshot);

        // Property Change resets geocode cache flag
        widget.Location = "Tokyo";
        Assert.AreEqual("Tokyo", widget.Location);
    }

    [TestMethod]
    public void WeatherForecastWidget_TouchInteractivity_CyclesLayoutAndUnits()
    {
        var widget = new WeatherForecastWidget();
        Assert.AreEqual("Detailed", widget.LayoutMode);

        // Touch top-left (Layout cycle)
        widget.OnTouch(new SKPoint(20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Daily Forecast", widget.LayoutMode);

        widget.OnTouch(new SKPoint(20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Hourly Forecast", widget.LayoutMode);

        widget.OnTouch(new SKPoint(20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Current Only", widget.LayoutMode);

        widget.OnTouch(new SKPoint(20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Compact", widget.LayoutMode);

        widget.OnTouch(new SKPoint(20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Detailed", widget.LayoutMode);

        // Touch top-right (Unit switch)
        widget.OnTouch(new SKPoint(widget.DefaultSize.Width - 20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Celsius (°C, km/h)", widget.UnitSystem);

        widget.OnTouch(new SKPoint(widget.DefaultSize.Width - 20f, 15f), TouchEventType.TouchUp);
        Assert.AreEqual("Fahrenheit (°F, mph)", widget.UnitSystem);
    }

    [TestMethod]
    public void WeatherForecastWidget_Rendering_ExecutesWithoutExceptions()
    {
        var widget = new WeatherForecastWidget();
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        var canvas = surface.Canvas;
        var bounds = new SKRect(0, 0, 400, 300);

        string[] modes = ["Detailed", "Daily Forecast", "Hourly Forecast", "Current Only", "Compact"];
        foreach (var mode in modes)
        {
            widget.LayoutMode = mode;
            widget.Render(canvas, bounds);
        }

        Assert.IsNotNull(surface);
    }

    [TestMethod]
    public void WeatherForecastWidget_SmallGridSizeScaling_ExecutesWithoutExceptions()
    {
        var widget = new WeatherForecastWidget();
        using var surface = SKSurface.Create(new SKImageInfo(200, 160));
        var canvas = surface.Canvas;

        SKSize[] smallSizes = [new(200, 160), new(150, 120), new(120, 90)];
        foreach (var size in smallSizes)
        {
            widget.Render(canvas, new SKRect(0, 0, size.Width, size.Height));
        }

        Assert.IsNotNull(surface);
    }
}
