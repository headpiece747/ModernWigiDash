using System.Net.Http;
using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Widgets;
using ModernWigiDash.Sdk;
using SkiaSharp;

namespace ModernWigiDash.Tests;

[TestClass]
public class WeatherForecastWidgetTests
{
    internal const string SampleForecast = """
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
    public async Task LocationMatchOptions_AfterGeocode_ExposeCandidatesWithAutomaticEntry()
    {
        const string multi = """
        {
          "results": [
            { "name": "Victoria", "latitude": 48.4284, "longitude": -123.3656, "admin1": "British Columbia", "country": "Canada", "country_code": "CA", "population": 335696 },
            { "name": "Vit\u00f3ria", "latitude": -20.3194, "longitude": -40.3378, "admin1": "Esp\u00edrito Santo", "country": "Brazil", "country_code": "BR", "population": 1962476 }
          ]
        }
        """;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("/v1/search", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(multi)
                : StubHttpHandler.Ok(SampleForecast);
        });
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(stub), Location = "Victoria" };

        await widget.FetchLiveWeatherAsync(force: true);

        var options = widget.GetPropertyOptions(nameof(WeatherForecastWidget.LocationMatch));
        Assert.AreEqual(3, options.Count, "Automatic entry + 2 candidates");
        Assert.AreEqual("", options[0].Value, "The first option must be the empty 'Automatic' entry so a pick can be cleared");
        CollectionAssert.Contains(
            options.Select(o => o.Value).ToArray(),
            "Victoria, British Columbia, Canada",
            "The exact-match candidate must be pickable by its label");
        CollectionAssert.Contains(
            options.Select(o => o.DisplayName).ToArray(),
            "Vitória, Espírito Santo, Brazil",
            "The alternative candidate must be pickable by its label");
    }

    [TestMethod]
    public async Task LocationMatchPick_ChangingLocation_ClearsCandidatesAndRegeocodes()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal))
            {
                return StubHttpHandler.Ok(url.Contains("name=Berlin", StringComparison.OrdinalIgnoreCase)
                    ? """{ "results": [ { "name": "Berlin", "latitude": 52.52, "longitude": 13.405, "country": "Germany" } ] }"""
                    : """
                    {
                      "results": [
                        { "name": "Victoria", "latitude": 48.4284, "longitude": -123.3656, "admin1": "British Columbia", "country": "Canada", "country_code": "CA", "population": 335696 },
                        { "name": "Vit\u00f3ria", "latitude": -20.3194, "longitude": -40.3378, "admin1": "Esp\u00edrito Santo", "country": "Brazil", "country_code": "BR", "population": 1962476 }
                      ]
                    }
                    """);
            }
            return StubHttpHandler.Ok(SampleForecast);
        });
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(stub), Location = "Victoria" };

        await widget.FetchLiveWeatherAsync(force: true); // geocode(1) + forecast(2)

        // Picking a candidate resolves to it; then changing the Location
        // (via OnPropertyChanged, the inspector's write-back path) must drop
        // the candidates and re-geocode the new city — the stale pick must not win.
        // options[0] = "Automatic", options[1] = Victoria (Canada), options[2] = Vitoria (Brazil)
        string picked = widget.GetPropertyOptions(nameof(WeatherForecastWidget.LocationMatch))[2].Value;
        widget.LocationMatch = picked;
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.LocationMatch), picked);
        // Wait for the pick fetch to COMPLETE (FetchCompletedCount advances
        // only when FetchCurrentAsync finishes and releases its in-flight
        // claim) — the HTTP call count alone fires mid-fetch and races the
        // next change.
        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 2, TimeSpan.FromSeconds(5));
        Assert.AreEqual("Vitória, Espírito Santo, Brazil", widget.ResolvedCityName);

        widget.Location = "Berlin";
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.Location), "Berlin");
        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 3, TimeSpan.FromSeconds(5));
        Assert.AreEqual("Berlin, Germany", widget.ResolvedCityName);
    }

    [TestMethod]
    public async Task LocationMatchOptions_ClearPick_RevertsToAutoRanking()
    {
        const string multi = """
        {
          "results": [
            { "name": "Victoria", "latitude": 48.4284, "longitude": -123.3656, "admin1": "British Columbia", "country": "Canada", "country_code": "CA", "population": 335696 },
            { "name": "Vit\u00f3ria", "latitude": -20.3194, "longitude": -40.3378, "admin1": "Esp\u00edrito Santo", "country": "Brazil", "country_code": "BR", "population": 1962476 }
          ]
        }
        """;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("/v1/search", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(multi)
                : StubHttpHandler.Ok(SampleForecast);
        });
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(stub), Location = "Victoria" };

        await widget.FetchLiveWeatherAsync(force: true); // geocode(1) + forecast(2)

        // options[0] = "Automatic", options[1] = Victoria (Canada), options[2] = Vitoria (Brazil)
        string picked = widget.GetPropertyOptions(nameof(WeatherForecastWidget.LocationMatch))[2].Value;
        widget.LocationMatch = picked;
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.LocationMatch), picked);
        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 2 && widget.Location == "Vitória, Espírito Santo, Brazil", TimeSpan.FromSeconds(5));
        Assert.AreEqual("Vitória, Espírito Santo, Brazil", widget.ResolvedCityName);
        Assert.AreEqual("Vitória, Espírito Santo, Brazil", widget.Location,
            "the write-back must leave the field showing the picked place");

        // Clearing the pick (the empty "Automatic" option) reverts to ranking.
        // The write-back made Location carry the picked label, so the label
        // self-resolves to the same city; returning the field to the bare
        // query then lets the auto ranking of "Victoria" decide — a stale
        // pick must never win it.
        widget.LocationMatch = "";
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.LocationMatch), "");
        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 3, TimeSpan.FromSeconds(5));
        widget.Location = "Victoria";
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.Location), "Victoria");
        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 4, TimeSpan.FromSeconds(5));
        Assert.AreEqual("Victoria, British Columbia, Canada", widget.ResolvedCityName);
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
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
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

    [TestMethod]
    public void CommitPick_SetsOnlyLocation()
    {
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())) };
        var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
        var profile = new ProfileLayout();
        profile.ActivePage.Widgets.Add(placed);
        var context = new PersistingContext(profile);
        widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();

        var search = (IWidgetLocationSearch)widget;
        search.CommitPick(new GeocodeCandidate("Berlin, New Hampshire, United States", "Berlin, New Hampshire, United States", 44.46867, -71.18508));

        Assert.AreEqual("Berlin, New Hampshire, United States", widget.Location);
        Assert.AreEqual("", widget.Latitude, "Lat/Lon must stay manual-only — never filled by a pick");
        Assert.AreEqual("", widget.Longitude);
        Assert.IsTrue(placed.PropertyValues.ContainsKey("Location"), "the pick must persist the label");
        Assert.IsFalse(placed.PropertyValues.ContainsKey("Latitude"), "no Lat/Lon may be persisted by a pick");
    }

    [TestMethod]
    public async Task FetchLiveWeatherAsync_AmbiguousName_KeepsStateSilently()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherClientTests.SampleBerlines);
            return StubHttpHandler.NotFound();
        });
        var widget = new WeatherForecastWidget { Location = "Berlin" };
        widget.TestHttpClient = new HttpClient(stub);
        var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
        var profile = new ProfileLayout();
        profile.ActivePage.Widgets.Add(placed);
        var context = new PersistingContext(profile);
        widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
        var renders = context.Renders;

        // The shipped prompt state is gone: an ambiguous resolution must be
        // silent — no fetch, no state change, no render request.
        // (Drive the fetch directly; the client's ambiguity flag suppresses it.)
        widget._suppressLocationWriteback = true; // isolate this test from the write-back path
        await widget.FetchLiveWeatherAsync();

        Assert.AreEqual(renders, context.Renders, "no render request for an ambiguous resolution");
        Assert.AreEqual(0, stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)),
            "no forecast request for an ambiguous name — the gate is silent");
    }

    [TestMethod]
    public async Task FetchLiveWeatherAsync_ResolvedLabel_IsWrittenBackToLocation()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherClientTests.SampleSameNameMultiCountry);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var widget = new WeatherForecastWidget { Location = "Victoria" };
        widget.TestHttpClient = new HttpClient(stub);
        var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
        var profile = new ProfileLayout();
        profile.ActivePage.Widgets.Add(placed);
        var context = new PersistingContext(profile);
        widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
        // InitializeAsync kicks the startup fetch; its write-back runs on the
        // fetch's continuation (after the client's FetchCompletedCount
        // advances), so wait for the written-back label itself — the same
        // settle pattern the pick tests use.
        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 1 && widget.Location == "Victoria, British Columbia, Canada", TimeSpan.FromSeconds(5));

        await widget.FetchLiveWeatherAsync();

        Assert.AreEqual("Victoria, British Columbia, Canada", widget.Location,
            "a successful resolution must write the resolved label back into Location");
        Assert.IsTrue(placed.PropertyValues.ContainsKey("Location"));
        Assert.AreEqual(1, stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)),
            "the write-back must not loop: exactly one forecast request");
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

        // Touch the unit badge — the badge rect is the tap target (WeatherLayout
        // owns the geometry; take the badge's center at the fallback bounds).
        var fallback = new SKRect(0, 0, widget.DefaultSize.Width, widget.DefaultSize.Height);
        var scale = WeatherLayout.Scale(fallback);
        var badge = WeatherLayout.ComputeHeader(fallback, scale.S, scale.Sy).BadgeRect;
        SKPoint badgeTap = new(badge.MidX, badge.MidY);

        widget.OnTouch(badgeTap, TouchEventType.TouchUp);
        Assert.AreEqual("Celsius (°C, km/h)", widget.UnitSystem);

        widget.OnTouch(badgeTap, TouchEventType.TouchUp);
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
