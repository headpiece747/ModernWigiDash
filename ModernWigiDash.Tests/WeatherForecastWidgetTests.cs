using System.Net.Http;
using System.Reflection;
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
        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 2, TimeSpan.FromSeconds(5));
        // The write-back lands on the UI thread (the Render flush), never on
        // the fetch continuation — Context.PersistProperty stays on the UI
        // thread and a stale fetch cannot clobber a newer edit.
        widget.ApplyPendingLocationWriteback();
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
        widget.ApplyPendingLocationWriteback();
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
        // InitializeAsync kicks the startup fetch; wait for it to complete.
        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 1, TimeSpan.FromSeconds(5));
        // The write-back lands on the UI thread (the Render flush), never on
        // the fetch continuation — Context.PersistProperty stays on the UI
        // thread and a stale fetch cannot clobber a newer edit.
        widget.ApplyPendingLocationWriteback();

        Assert.AreEqual("Victoria, British Columbia, Canada", widget.Location,
            "a successful resolution must write the resolved label back into Location");
        Assert.IsTrue(placed.PropertyValues.ContainsKey("Location"));
        Assert.AreEqual(1, stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)),
            "the write-back must not loop: exactly one forecast request");
    }

    [TestMethod]
    public async Task FetchLiveWeatherAsync_InFlightLocationEdit_DoesNotClobberTheEdit()
    {
        // A fetch that was in flight while the user edited Location must never
        // write the stale resolved label over the newer edit.
        var gate = new TaskCompletionSource();
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("/v1/search", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(WeatherClientTests.SampleSameNameMultiCountry)
                : StubHttpHandler.Ok(SampleForecast);
        }, gate);
        var widget = new WeatherForecastWidget { Location = "Victoria" };
        widget.TestHttpClient = new HttpClient(stub);

        var fetching = widget.FetchLiveWeatherAsync(force: true);
        await TestWait.WaitUntilAsync(() => stub.Calls >= 1, TimeSpan.FromSeconds(5));
        widget.Location = "Tokyo"; // the user edits while the fetch is in flight
        gate.SetResult();
        await fetching;
        widget.ApplyPendingLocationWriteback();

        Assert.AreEqual("Tokyo", widget.Location,
            "a stale in-flight resolution must never clobber a newer edit");
    }

    [TestMethod]
    public async Task FetchLiveWeatherAsync_InFlightCountryCodeEdit_DoesNotClobberTheEditOrApplyStaleWeather()
    {
        // A fetch in flight while the user edited CountryCode must neither
        // apply the stale resolution's weather nor write its stale label —
        // the identity guard covers every invalidation source, not just
        // Location edits. The stale result is dropped and the new identity
        // (with the country hint) is fetched immediately, because the edit's
        // force refresh was swallowed by the in-flight claim.
        var gate = new TaskCompletionSource();
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (!url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleForecast);
            return url.Contains("countryCode=DE", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(WeatherClientTests.SampleSanJoses)
                : StubHttpHandler.Ok(WeatherClientTests.SampleSameNameMultiCountry);
        }, gate);
        var widget = new WeatherForecastWidget { Location = "Victoria" };
        widget.TestHttpClient = new HttpClient(stub);

        var fetching = widget.FetchLiveWeatherAsync(force: true);
        await TestWait.WaitUntilAsync(() => stub.Calls >= 1, TimeSpan.FromSeconds(5));
        widget.CountryCode = "DE"; // the user adds a country hint while the fetch is in flight
        gate.SetResult();
        await fetching;
        widget.ApplyPendingLocationWriteback();

        Assert.AreEqual("Victoria", widget.Location,
            "a stale in-flight resolution must never write its label over the newer edit");
        Assert.AreEqual("DE", widget.CountryCode, "the country hint edit must survive");
        // The stale result was dropped and the new identity re-fetched: the
        // second search carries the country hint (the old identity's search
        // did not), and its unresolvable result applied nothing.
        Assert.IsTrue(stub.RequestUrls.Any(u => u.Contains("/v1/search", StringComparison.Ordinal) && u.Contains("countryCode=DE", StringComparison.Ordinal)),
            "the new identity must be fetched with the country hint");
    }

    [TestMethod]
    public async Task FetchLiveWeatherAsync_CancelledToken_ReturnsSilently()
    {
        // Teardown (dispose cancels the poll CTS) must abort a fetch without
        // logging an error or touching the network — the geocode and forecast
        // legs propagate the cancellation, and the widget swallows it.
        var stub = new StubHttpHandler(_ => StubHttpHandler.Ok(SampleForecast));
        var widget = new WeatherForecastWidget { Location = "Victoria" };
        widget.TestHttpClient = new HttpClient(stub);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        widget._pollCts = cts;

        await widget.FetchLiveWeatherAsync(force: true);

        // The cancellation is swallowed by the widget (teardown is not a
        // failure): awaiting the fetch completes without throwing.
        widget.ApplyPendingLocationWriteback();
        Assert.AreEqual("Victoria", widget.Location, "a cancelled fetch must not apply or write back anything");
    }

    [TestMethod]
    public void OnPropertyChanged_InvalidationSet_MatchesWeatherLocationKeyFields()
    {
        // BuildQueryKey interpolates every WeatherLocation property except
        // CustomLabel; the widget's invalidation guard (plus LocationMatch's
        // own branch) must cover exactly those fields — otherwise a new
        // resolution input would change the identity without forcing a
        // re-fetch, and the "one guard covers every source" claim rots.
        var keyFields = typeof(WeatherLocation)
            .GetProperties()
            .Select(p => p.Name)
            .Where(name => name != nameof(WeatherLocation.CustomLabel))
            .ToHashSet();
        var guarded = WeatherForecastWidget.ResolutionInvalidationProperties
            .Append(nameof(WeatherLocation.LocationMatch))
            .ToHashSet();

        CollectionAssert.AreEquivalent(keyFields.ToList(), guarded.ToList(),
            "every WeatherLocation key field must be covered by the invalidation guard or LocationMatch's branch");
    }

    [TestMethod]
    public async Task CommitPick_ResolvesAsExactlyOneForecastFetch()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherClientTests.SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(SampleForecast) : StubHttpHandler.NotFound();
        });
        var widget = new WeatherForecastWidget { Location = "Berlin" };
        widget.TestHttpClient = new HttpClient(stub);
        var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
        var profile = new ProfileLayout();
        profile.ActivePage.Widgets.Add(placed);
        var context = new PersistingContext(profile);
        widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
        // Startup: the bare "Berlin" ties on the exact name (search only, no
        // forecast) — wait for that to settle before the pick.
        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 1, TimeSpan.FromSeconds(5));

        ((IWidgetLocationSearch)widget).CommitPick(new GeocodeCandidate("Berlin, New Hampshire, United States", "Berlin, New Hampshire, United States", 44.46867, -71.18508));
        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 2, TimeSpan.FromSeconds(5));

        Assert.AreEqual(1, stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)),
            "a pick must resolve with exactly one forecast request (search + forecast, no loop)");
    }

    [TestMethod]
    public void WeatherForecastWidget_DefaultsAndProperties_InitializeCorrectly()
    {
        var widget = new WeatherForecastWidget();

        Assert.AreEqual("Miami, Florida", widget.Location);
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
    public void CacheFileName_KeysBy_CurrentInstanceId()
    {
        var widget = new WeatherForecastWidget();
        string placedId = Guid.NewGuid().ToString();

        // RehydrateWidget assigns the placed InstanceId only after the widget
        // is constructed; the cache name must follow that final identity, so
        // the file round-trips across restarts (a construction-time GUID would
        // orphan every write under a never-reused filename).
        widget.InstanceId = placedId;

        Assert.AreEqual($"weather_{placedId}.json", widget.CacheFileName);
    }

    [TestMethod]
    public void UnitSystemAttribute_Options_AllParseToTheirOwnUnits()
    {
        // The inspector's choice list must stay in lockstep with the parser:
        // a corrupted or renamed option (the °→U+FFFD corruption that made
        // re-selecting Fahrenheit silently render Celsius) falls through to
        // the default units.
        var attribute = typeof(WeatherForecastWidget)
            .GetProperty(nameof(WeatherForecastWidget.UnitSystem))!
            .GetCustomAttribute<WidgetPropertyAttribute>()!;
        var expected = new Dictionary<string, (string, string)>
        {
            ["Fahrenheit (°F, mph)"] = ("°F", "mph"),
            ["Celsius (°C, km/h)"] = ("°C", "km/h"),
            ["Celsius (°C, mph)"] = ("°C", "mph"),
            ["Celsius (°C, m/s)"] = ("°C", "m/s"),
            ["Kelvin (K, m/s)"] = ("K", "m/s"),
        };

        Assert.AreEqual(expected.Count, attribute.Options.Length, "every choice option must be pinned");
        foreach (string option in attribute.Options)
        {
            Assert.IsTrue(expected.TryGetValue(option, out var units),
                $"option '{option}' must be a known parseable string");
            Assert.AreEqual(units, WeatherPresentation.ParseUnitSystem(option));
        }
        Assert.AreEqual(WeatherPresentation.DefaultUnitSystem, attribute.DefaultValue,
            "the attribute default must match the property default and the parser's primary case");
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
