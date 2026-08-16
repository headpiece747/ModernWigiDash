using System.IO;
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
    // The forecast fixture is shared from WeatherClientTests
    // (SampleForecastLegacy): the widget tests deliberately ride the LEGACY
    // response shape (current_weather + relativehumidity_2m + weathercode):
    // the client tests own the canonical fixture, so the widget tests never
    // carry a divergent copy. The 19+2 references below spell the owner out
    // for the same reason.

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
                : StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
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
                : StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
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
            return StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
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
        // Wait for the APPLIED state, not the client's fetch-completion count:
        // the count increments inside the client's finally BEFORE the widget's
        // continuation applies the snapshot, so a count-based wait can race
        // the apply on a busy scheduler.
        await TestWait.WaitUntilAsync(() => widget.ResolvedCityName == "Vitória, Espírito Santo, Brazil", TimeSpan.FromSeconds(5));
        Assert.AreEqual("Vitória, Espírito Santo, Brazil", widget.ResolvedCityName);

        widget.Location = "Berlin";
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.Location), "Berlin");
        await TestWait.WaitUntilAsync(() => widget.ResolvedCityName == "Berlin, Germany", TimeSpan.FromSeconds(5));
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
                : StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
        });
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(stub), Location = "Victoria" };

        await widget.FetchLiveWeatherAsync(force: true); // geocode(1) + forecast(2)
        // Deterministic write-back flush: FetchCompletedCount increments in
        // the client before the widget's continuation sets the pending
        // write-back, so waiting on the count alone lets fetch #1's
        // continuation land late and clobber fetch #2's — wait for the
        // applied state, then flush.
        await TestWait.WaitUntilAsync(() => widget.ResolvedCityName == "Victoria, British Columbia, Canada", TimeSpan.FromSeconds(5));
        widget.ApplyPendingLocationWriteback();
        Assert.AreEqual("Victoria, British Columbia, Canada", widget.Location);

        // options[0] = "Automatic", options[1] = Victoria (Canada), options[2] = Vitoria (Brazil)
        string picked = widget.GetPropertyOptions(nameof(WeatherForecastWidget.LocationMatch))[2].Value;
        widget.LocationMatch = picked;
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.LocationMatch), picked);
        // Wait for the APPLIED state - the pick's label landed in Location via
        // the write-back - not the client's fetch-completion count: the count
        // increments inside the client's finally BEFORE the widget's
        // continuation sets the pending write-back, so a count-based wait can
        // flush early and leave Location unchanged. The flush inside the
        // predicate is idempotent and re-checks each poll until it lands.
        await TestWait.WaitUntilAsync(() =>
        {
            widget.ApplyPendingLocationWriteback();
            return string.Equals(widget.Location, "Vitória, Espírito Santo, Brazil", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));
        Assert.AreEqual("Vitória, Espírito Santo, Brazil", widget.ResolvedCityName);
        Assert.AreEqual("Vitória, Espírito Santo, Brazil", widget.Location,
            "the write-back must leave the field showing the picked place");

        // Clearing the pick (the empty "Automatic" option) reverts to ranking.
        // The write-back made Location carry the picked label, so the label
        // self-resolves to the same city; returning the field to the bare
        // query then lets the auto ranking of "Victoria" decide - a stale
        // pick must never win it. (No completion-count wait between the
        // changes: if the clear-pick fetch is still in flight when Location
        // changes, its stale result is dropped and the forced re-fetch covers
        // the new identity.)
        widget.LocationMatch = "";
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.LocationMatch), "");
        widget.Location = "Victoria";
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.Location), "Victoria");
        await TestWait.WaitUntilAsync(() => widget.ResolvedCityName == "Victoria, British Columbia, Canada", TimeSpan.FromSeconds(5));
        widget.ApplyPendingLocationWriteback();
        Assert.AreEqual("Victoria, British Columbia, Canada", widget.ResolvedCityName);
    }

    [TestMethod]
    public async Task InitializeAsync_BootFetch_FiresImmediately()
    {
        // The boot fetch exists so hidden-page widgets (fresh starter
        // profiles — no property hydration, no render kick) get weather
        // without waiting for the 5-minute poll tick. The identity guard
        // (FetchLiveWeatherAsync_InFlightCountryCodeEdit_*) drops any result
        // whose location changed while in flight, so the boot fetch can
        // never display a stale city.
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok("""{"results":[{"name":"Paris","latitude":48.85,"longitude":2.35,"admin1":"Ile-de-France","country":"France","country_code":"FR"}]}""");
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy) : StubHttpHandler.NotFound();
        });
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(stub), Location = "Paris, France" };

        await widget.InitializeAsync(new TestContext());

        await TestWait.WaitUntilAsync(() => widget.FetchCompletedCount >= 1, TimeSpan.FromSeconds(5));
        Assert.IsTrue(stub.RequestUrls.Any(u => u.Contains("name=Paris", StringComparison.Ordinal)),
            "the boot fetch must use the widget's current location");
    }

    [TestMethod]
    public async Task FetchLiveWeather_Throttle_UsesInjectedClock()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("/v1/search", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(SampleGeocode)
                : StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
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
    public async Task StaticSnapshot_NonForcedRefresh_IsGatedUntilToggledOff()
    {
        // The static-snapshot rule: while a snapshot is showing (a primed
        // fetch time), the render kick's non-forced refresh must not fetch -
        // the cadence gate is the single RequestRefresh decision point, and
        // the render tick drives it. Toggling the property off resumes the
        // cadence.
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleGeocode);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy) : StubHttpHandler.NotFound();
        });
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero));
        var widget = new WeatherForecastWidget { Location = "New York", TestHttpClient = new HttpClient(stub), Clock = clock };
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var bounds = new SKRect(0, 0, 406, 296);

        // Prime the fetch time (a snapshot exists) and open the throttle window.
        await widget.FetchLiveWeatherAsync(force: true);
        clock.Advance(TimeSpan.FromMinutes(6));
        int forecastsAfterPrime = stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal));

        widget.StaticSnapshot = true;
        widget.Render(surface.Canvas, bounds); // the render kick's non-forced RequestRefresh
        Assert.AreEqual(forecastsAfterPrime, stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)),
            "a static snapshot must gate the render kick's non-forced refresh");

        widget.StaticSnapshot = false;
        widget.Render(surface.Canvas, bounds);
        await TestWait.WaitUntilAsync(
            () => stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)) > forecastsAfterPrime,
            TimeSpan.FromSeconds(5));
        Assert.IsTrue(stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)) > forecastsAfterPrime,
            "toggling the snapshot off must resume the fetch cadence");
    }

    [TestMethod]
    public void RenderModel_UnchangedState_ReusesTheCachedModel()
    {
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())) };
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var bounds = new SKRect(0, 0, 406, 296);

        widget.Render(surface.Canvas, bounds);
        var first = widget._renderModel;
        Assert.IsNotNull(first, "the first render must build a model");

        widget.Render(surface.Canvas, bounds);

        Assert.AreSame(first, widget._renderModel, "unchanged state must reuse the cached render model");
        Assert.AreSame(first.MetricWidths, widget._renderModel!.MetricWidths,
            "the cached pill widths must be reused, not re-measured");
    }

    [TestMethod]
    public void RenderModel_EachCacheKey_ForcesARebuild()
    {
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(new StubHttpHandler(_ => StubHttpHandler.NotFound())) };
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var bounds = new SKRect(0, 0, 406, 296);

        // The matrix pins every IsCacheValid key: changing any one of them
        // must rebuild the model (the static scene would otherwise draw stale
        // strings). Each mutation persists — every assertion starts from the
        // previous mutated state and changes ONE key.
        void AssertRebuilds(string keyName, Action mutate)
        {
            widget.Render(surface.Canvas, bounds);
            var before = widget._renderModel;
            mutate();
            widget.Render(surface.Canvas, bounds);
            Assert.AreNotSame(before, widget._renderModel, $"{keyName} must force a rebuild");
        }

        AssertRebuilds("LayoutMode", () => widget.LayoutMode = "Compact");
        AssertRebuilds("UnitSystem", () => widget.UnitSystem = "Celsius (°C, km/h)");
        AssertRebuilds("CustomLabel", () => widget.CustomLabel = "Home");
        AssertRebuilds("ShowFeelsLike", () => widget.ShowFeelsLike = false);
        AssertRebuilds("ShowHumidity", () => widget.ShowHumidity = false);
        AssertRebuilds("ShowWind", () => widget.ShowWind = false);
        AssertRebuilds("ShowHighLow", () => widget.ShowHighLow = false);
        AssertRebuilds("ShowForecast", () => widget.ShowForecast = false);
        AssertRebuilds("Bounds", () => bounds = new SKRect(0, 0, 300, 200));
    }

    [TestMethod]
    public async Task RenderModel_FetchChangesDataVersionAndResolvedCity_ForcesARebuild()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("/v1/search", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(SampleGeocode)
                : StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
        });
        var widget = new WeatherForecastWidget { Location = "New York", TestHttpClient = new HttpClient(stub) };
        using var surface = SKSurface.Create(new SKImageInfo(406, 296));
        var bounds = new SKRect(0, 0, 406, 296);

        widget.Render(surface.Canvas, bounds);
        // The render's own kick may have fired a fetch (the stub completes
        // synchronously) - wait for it to APPLY (the daily rows land in the
        // widget's lists) so `before` is stable. The client's completion
        // count increments before the widget's continuation applies the
        // snapshot, so a count-based wait can race the apply.
        await TestWait.WaitUntilAsync(() => widget._dailyForecasts.Count >= 2, TimeSpan.FromSeconds(5));
        var before = widget._renderModel;
        Assert.IsNotNull(before);

        await widget.FetchLiveWeatherAsync(force: true);
        widget.Render(surface.Canvas, bounds);

        Assert.AreNotSame(before, widget._renderModel, "a fetch (data version + resolved city) must force a rebuild");
        Assert.AreEqual("New York, US", widget._renderModel!.ResolvedCity);
    }

    [TestMethod]
    public async Task FetchLiveWeatherAsync_MalformedForecastBody_KeepsPreviousForecast()
    {
        bool fail = false;
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(SampleGeocode);
            return fail ? StubHttpHandler.Ok("not json {{{") : StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
        });
        var widget = new WeatherForecastWidget { Location = "New York", TestHttpClient = new HttpClient(stub) };

        await widget.FetchLiveWeatherAsync(force: true);
        int dailyCount = widget._dailyForecasts.Count;
        Assert.IsTrue(dailyCount >= 2, "precondition: the first fetch applies the forecast");

        fail = true;
        await widget.FetchLiveWeatherAsync(force: true);

        Assert.AreEqual(dailyCount, widget._dailyForecasts.Count,
            "a malformed body must keep the previous forecast intact");
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

        // An ambiguous resolution must be silent - no fetch, no state change,
        // no render request.
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
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy) : StubHttpHandler.NotFound();
        });
        var widget = new WeatherForecastWidget { Location = "Victoria" };
        widget.TestHttpClient = new HttpClient(stub);
        var placed = new PlacedWidgetInstance { PluginId = "weather", ActiveInstance = widget };
        var profile = new ProfileLayout();
        profile.ActivePage.Widgets.Add(placed);
        var context = new PersistingContext(profile);
        widget.InitializeAsync(context).AsTask().GetAwaiter().GetResult();
        // Poll the write-back application until the label lands: the
        // fetch-completion count increments inside the client's finally BEFORE
        // the widget's continuation sets the pending write-back, so a
        // count-based wait can race the apply on a busy scheduler. The apply
        // is idempotent — a no-op while the pending field is not yet set.
        await TestWait.WaitUntilAsync(() =>
        {
            widget.ApplyPendingLocationWriteback();
            return string.Equals(widget.Location, "Victoria, British Columbia, Canada", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));

        Assert.AreEqual("Victoria, British Columbia, Canada", widget.Location,
            "a successful resolution must write the resolved label back into Location");
        Assert.IsTrue(placed.PropertyValues.ContainsKey("Location"));
        Assert.AreEqual(1, stub.RequestUrls.Count(u => u.Contains("/v1/forecast", StringComparison.Ordinal)),
            "the write-back must not loop: exactly one forecast request");
    }

    [TestMethod]
    public async Task FetchLiveWeatherAsync_CustomLabel_SkipsTheLocationWriteback()
    {
        // A CustomLabel supplies the title; the resolved name must never be
        // written back into Location (writing "Home" into the field would
        // destroy the query: explicit-coords/pick + CustomLabel would
        // overwrite "New York" with "Home" in the profile). Explicit
        // coordinates skip geocoding, so the fetch is one forecast leg.
        var stub = new StubHttpHandler(_ => StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy));
        var widget = new WeatherForecastWidget
        {
            Location = "New York",
            Latitude = "40.7128",
            Longitude = "-74.006",
            CustomLabel = "Home",
            TestHttpClient = new HttpClient(stub),
        };

        await widget.FetchLiveWeatherAsync(force: true);
        widget.ApplyPendingLocationWriteback();

        Assert.AreEqual("New York", widget.Location,
            "the resolved name must never overwrite the query when a CustomLabel supplies the title");
        Assert.AreEqual("Home", widget.ResolvedCityName,
            "the applied snapshot's resolved name is the CustomLabel (the client-side resolution honors it)");
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
                : StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
        }, gate);
        var widget = new WeatherForecastWidget { Location = "Victoria" };
        widget.TestHttpClient = new HttpClient(stub);

        var fetching = widget.FetchLiveWeatherAsync(force: true);
        await TestWait.WaitUntilAsync(() => stub.Calls >= 1, TimeSpan.FromSeconds(5));
        widget.Location = "Tokyo"; // the user edits while the fetch is in flight
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.Location), "Tokyo"); // the real edit path — invalidates the client
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
            if (!url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
            return url.Contains("countryCode=DE", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(WeatherClientTests.SampleSanJoses)
                : StubHttpHandler.Ok(WeatherClientTests.SampleSameNameMultiCountry);
        }, gate);
        var widget = new WeatherForecastWidget { Location = "Victoria" };
        widget.TestHttpClient = new HttpClient(stub);

        var fetching = widget.FetchLiveWeatherAsync(force: true);
        await TestWait.WaitUntilAsync(() => stub.Calls >= 1, TimeSpan.FromSeconds(5));
        widget.CountryCode = "DE"; // the user adds a country hint while the fetch is in flight
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.CountryCode), "DE"); // the real edit path — invalidates the client
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
    public async Task FetchLiveWeatherAsync_LocationReassignedWithoutNotification_DropsStaleAndRefetches()
    {
        // RehydrateWidget assigns the profile's properties AFTER the widget is
        // constructed and never calls OnPropertyChanged for them - the silent
        // assignment bypasses the widget's invalidation, so a fetch that was
        // in flight during it completes as Fetched (the client's identity
        // never saw the edit). The widget's post-await re-validation must
        // drop that stale result AND force a re-fetch of the new identity,
        // exactly like the OnPropertyChanged path does for live edits.
        var gate = new TaskCompletionSource();
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal))
            {
                return StubHttpHandler.Ok(url.Contains("name=Tokyo", StringComparison.OrdinalIgnoreCase)
                    ? """{ "results": [ { "name": "Tokyo", "latitude": 35.6762, "longitude": 139.6503, "admin1": "Tokyo Prefecture", "country": "Japan", "country_code": "JP" } ] }"""
                    : WeatherClientTests.SampleSameNameMultiCountry);
            }
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy) : StubHttpHandler.NotFound();
        }, gate);
        var widget = new WeatherForecastWidget { Location = "Victoria", TestHttpClient = new HttpClient(stub) };

        var fetching = widget.FetchLiveWeatherAsync(force: true);
        await TestWait.WaitUntilAsync(() => stub.Calls >= 1, TimeSpan.FromSeconds(5));
        widget.Location = "Tokyo"; // the rehydration-style assignment: NO OnPropertyChanged
        gate.SetResult();
        await fetching;

        // The re-validation must have dropped the Victoria result (never
        // applied) and re-fetched Tokyo: wait for the applied state of the
        // re-fetch, not the client's completion count (the count increments
        // before the widget's continuation applies the snapshot).
        await TestWait.WaitUntilAsync(() => widget.ResolvedCityName == "Tokyo, Tokyo Prefecture, Japan", TimeSpan.FromSeconds(5));
        Assert.AreEqual("Tokyo, Tokyo Prefecture, Japan", widget.ResolvedCityName,
            "a fetch that completed under a silently-reassigned identity must be dropped and re-fetched");
        Assert.IsTrue(stub.RequestUrls.Any(u => u.Contains("name=Tokyo", StringComparison.OrdinalIgnoreCase)),
            "the re-validation must re-fetch the NEW identity");
    }

    [TestMethod]
    public async Task FetchLiveWeatherAsync_CompletedFetchThenLocationEdit_WritebackDoesNotClobber()
    {
        // The edit-time race the pending write-back clear closes: a fetch
        // COMPLETED (not in flight) while the user edits Location, but its
        // resolved-label write-back had not been flushed by a Render tick yet.
        // The edit must drop the pending write-back, so the next flush leaves
        // the newer edit alone. (The in-flight stale guard only covers fetches
        // still in flight at edit time.)
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherClientTests.SampleSameNameMultiCountry);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy) : StubHttpHandler.NotFound();
        });
        var widget = new WeatherForecastWidget { Location = "Victoria", TestHttpClient = new HttpClient(stub) };

        // Fetch #1 completes and sets the pending write-back (no Render call
        // flushed it — the write-back waits for the UI thread).
        await widget.FetchLiveWeatherAsync(force: true);
        Assert.AreEqual("Victoria", widget.Location, "the write-back must still be pending at this point");

        // The user edits Location through the real property-change path
        // (SetProperty → OnPropertyChanged → invalidation + forced re-fetch).
        // The re-fetch for "Tokyo" resolves ambiguously (no such candidate)
        // and applies nothing, so the flush below is the only writer.
        widget.Location = "Tokyo";
        widget.OnPropertyChanged(nameof(WeatherForecastWidget.Location), "Tokyo");
        // Wait for the APPLIED state - the edit still stands after the flush -
        // not the client's completion count: the count increments inside the
        // client's finally BEFORE the widget's continuation runs, so a
        // count-based wait can flush while the re-fetch is still settling.
        // The flush is idempotent; the edit already cleared the pending
        // write-back, so the field can only stay "Tokyo".
        await TestWait.WaitUntilAsync(() =>
        {
            widget.ApplyPendingLocationWriteback();
            return string.Equals(widget.Location, "Tokyo", StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(5));

        widget.ApplyPendingLocationWriteback();
        Assert.AreEqual("Tokyo", widget.Location,
            "an edit made after a completed fetch must survive the pending write-back");
    }

    [TestMethod]
    public async Task LoadCachedWeatherAsync_FetchLandedDuringLoad_DoesNotOverwriteWithStaleCache()
    {
        // The boot race: InitializeAsync fires the cache load and the boot
        // fetch concurrently. When the fetch lands while the load is in
        // flight, the stale cache must not overwrite it — the load captures
        // the data version before its await and re-checks after.
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("/v1/search", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(SampleGeocode)
                : StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
        });
        var widget = new WeatherForecastWidget { Location = "New York", TestHttpClient = new HttpClient(stub) };

        // A stale cache that would clobber the fresh fetch (one daily row and
        // a garbage temperature; the fetch's shared legacy fixture carries two rows).
        var staleCache = new WeatherSnapshot(99.9, 99.9, 99, 99, 99, 99, 99,
            [new DailyForecastItem("Today", 99, 99, 0)], null, "Stale City", 0, 0);
        var gate = new TaskCompletionSource();
        widget.CacheLoadOverride = async (_, _) =>
        {
            await gate.Task.ConfigureAwait(false);
            return staleCache;
        };

        var loading = widget.LoadCachedWeatherAsync(CancellationToken.None); // captures version 0, parks at the gate
        await widget.FetchLiveWeatherAsync(force: true);                      // applies fresh data (version 1)
        gate.SetResult();                                                     // the load completes AFTER the fetch
        await loading;

        Assert.AreEqual(2, widget._dailyForecasts.Count,
            "the stale cache must not overwrite the fetch that landed during the load");
    }

    [TestMethod]
    public async Task LoadCachedWeatherAsync_NoConcurrentChange_AppliesTheCache()
    {
        var widget = new WeatherForecastWidget();
        var cached = new WeatherSnapshot(99.9, 99.9, 99, 99, 99, 99, 99,
            [new DailyForecastItem("Today", 99, 99, 0)], null, "Cached City", 0, 0);
        widget.CacheLoadOverride = (_, _) => Task.FromResult<WeatherSnapshot?>(cached);

        await widget.LoadCachedWeatherAsync(CancellationToken.None);

        Assert.AreEqual(1, widget._dailyForecasts.Count,
            "an unchanged data version must apply the cache (the guard only drops concurrent writes)");
    }

    [TestMethod]
    public async Task LoadCachedWeatherAsync_IdentityChangedMidLoad_RollsBackClientStateAndKeepsPreviousData()
    {
        // Rehydration-style boot race: the cache load captures the DEFAULT
        // location's key, then the profile silently reassigns Location (no
        // OnPropertyChanged). The default-stamped cache must not apply under
        // the new identity, and the client's resolution state must roll back
        // (InvalidateCoordinates) so the new identity starts clean: the
        // rollback resets the throttle, which is observable: a NON-forced
        // follow-up fetch runs immediately instead of cooling down.
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal))
            {
                return StubHttpHandler.Ok(url.Contains("name=Tokyo", StringComparison.OrdinalIgnoreCase)
                    ? """{ "results": [ { "name": "Tokyo", "latitude": 35.6762, "longitude": 139.6503, "admin1": "Tokyo Prefecture", "country": "Japan", "country_code": "JP" } ] }"""
                    : """{ "results": [ { "name": "Miami", "latitude": 25.7743, "longitude": -80.1937, "admin1": "Florida", "country": "United States", "country_code": "US" } ] }""");
            }
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy) : StubHttpHandler.NotFound();
        });
        var widget = new WeatherForecastWidget { TestHttpClient = new HttpClient(stub) };

        // Prime the client's throttle + data with the default location so the
        // rollback has a state to reset (and "keeps previous data" is
        // observable: two rows, not the cache's one).
        await widget.FetchLiveWeatherAsync(force: true);
        await TestWait.WaitUntilAsync(() => widget._dailyForecasts.Count >= 2, TimeSpan.FromSeconds(5));

        // The boot cache load: a snapshot whose identity belongs to the
        // DEFAULT location, released only after the silent reassignment.
        var cached = new WeatherSnapshot(99.9, 99.9, 99, 99, 99, 99, 99,
            [new DailyForecastItem("Today", 99, 99, 0)], null, "Cached City", 0, 0);
        var gate = new TaskCompletionSource();
        widget.CacheLoadOverride = async (_, _) =>
        {
            await gate.Task.ConfigureAwait(false);
            return cached;
        };

        var loading = widget.LoadCachedWeatherAsync(CancellationToken.None); // captures the DEFAULT key, parks at the gate
        widget.Location = "Tokyo"; // the rehydration-style assignment: NO OnPropertyChanged
        gate.SetResult();
        await loading;

        Assert.AreEqual(2, widget._dailyForecasts.Count,
            "a cache stamped for the previous identity must not apply under the silently-reassigned location");
        Assert.AreEqual("Miami, Florida, United States", widget.ResolvedCityName,
            "the discarded cache's resolved name must not surface (the widget keeps the previous resolution)");

        // The rollback reset the client's throttle: a NON-forced follow-up
        // fetch runs immediately (a stamped throttle would return Throttled
        // with no network hit) and resolves Tokyo fresh from its own
        // coordinates.
        int callsAfterRollback = stub.Calls;
        await widget.FetchLiveWeatherAsync();
        await TestWait.WaitUntilAsync(() => widget.ResolvedCityName == "Tokyo, Tokyo Prefecture, Japan", TimeSpan.FromSeconds(5));
        Assert.IsTrue(stub.Calls > callsAfterRollback,
            "the rollback must reset the throttle (LastFetchTimeUtc = MinValue) so the new identity fetches immediately");
        Assert.IsTrue(stub.RequestUrls.Any(u => u.Contains("name=Tokyo", StringComparison.OrdinalIgnoreCase)),
            "the follow-up fetch must resolve Tokyo fresh after the client rollback");
    }

    [TestMethod]
    public async Task LoadCachedWeatherAsync_NullDailySnapshot_KeepsPreviousForecast()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            return url.Contains("/v1/search", StringComparison.Ordinal)
                ? StubHttpHandler.Ok(SampleGeocode)
                : StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy);
        });
        var widget = new WeatherForecastWidget { Location = "New York", TestHttpClient = new HttpClient(stub) };
        await widget.FetchLiveWeatherAsync(force: true);
        Assert.AreEqual(2, widget._dailyForecasts.Count, "precondition: the fetch applies two daily rows");

        widget.CacheLoadOverride = (_, _) => Task.FromResult<WeatherSnapshot?>(
            new WeatherSnapshot(12.5, null, null, null, null, null, null, null, null, "Cached", 0, 0));

        await widget.LoadCachedWeatherAsync(CancellationToken.None);

        Assert.AreEqual(2, widget._dailyForecasts.Count,
            "a snapshot that omits the daily section must keep the previous forecast (null-Daily merge)");
    }

    [TestMethod]
    public async Task InitializeAsync_BootLoadAppliesLegacyCache_ThenBootFetchWins()
    {
        // End-to-end boot: a legacy (unstamped) cache file is applied by the
        // boot load through the widget's identity-threaded load, and the
        // concurrent boot fetch's fresh data wins afterwards.
        string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "weather_cache");
        Directory.CreateDirectory(cacheDir);
        var widget = new WeatherForecastWidget { InstanceId = "bootrace-id", Location = "Victoria" };
        widget.TestHttpClient = new HttpClient(new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherClientTests.SampleSameNameMultiCountry);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy) : StubHttpHandler.NotFound();
        }));
        string cachePath = Path.Combine(cacheDir, widget.CacheFileName);
        await File.WriteAllTextAsync(cachePath, """
        {
          "CurrentTempC": 99.9, "FeelsLikeC": 99, "Humidity": 99, "WindSpeedKmH": 99,
          "WeatherCode": 3, "HighTempC": 99, "LowTempC": 99, "ResolvedCityName": "Cached City",
          "Lat": 48.85, "Lon": 2.35,
          "DailyForecasts": [ { "DayName": "Today", "MaxTempC": 99, "MinTempC": 99, "WeatherCode": 0 } ],
          "HourlyForecasts": []
        }
        """);
        try
        {
            await widget.InitializeAsync(new TestContext());

            // The boot fetch's fresh data must be the final state (daily
            // WeatherCode 2 from the shared legacy fixture), regardless of whether the
            // load applied first or was dropped by the version guard.
            await TestWait.WaitUntilAsync(
                () => widget._dailyForecasts.Count == 2 && widget._dailyForecasts[0].WeatherCode == 2,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { File.Delete(cachePath); } catch { /* best-effort cleanup */ }
        }
    }

    [TestMethod]
    public async Task FetchLiveWeatherAsync_CancelledToken_ReturnsSilently()
    {
        // Teardown (dispose cancels the poll CTS) must abort a fetch without
        // logging an error or touching the network — the geocode and forecast
        // legs propagate the cancellation, and the widget swallows it.
        var stub = new StubHttpHandler(_ => StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy));
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
    public void OnPropertyChanged_InvalidationSet_CoversEveryResolutionInput()
    {
        // The client's stale detection rides the widget's invalidation calls:
        // a resolution input that fails to invalidate would let an in-flight
        // fetch apply stale weather after an edit. The expected set is
        // DERIVED from the WeatherLocation record (every member except
        // CustomLabel — a label change must not re-fetch): a new resolution
        // field added to the record fails this test until it is wired into
        // the invalidation set. LocationMatch has its own branch in
        // OnPropertyChanged (which clears the same client identity via
        // InvalidateCoordinates), so it is appended to the guarded side.
        var expected = typeof(WeatherLocation).GetProperties()
            .Select(p => p.Name)
            .Where(n => !string.Equals(n, nameof(WeatherLocation.CustomLabel), StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        var guarded = WeatherForecastWidget.ResolutionInvalidationProperties
            .Append(nameof(WeatherForecastWidget.LocationMatch))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEquivalent(expected, guarded,
            "every resolution input must invalidate the client identity (or route through LocationMatch's branch)");
    }

    [TestMethod]
    public async Task CommitPick_ResolvesAsExactlyOneForecastFetch()
    {
        var stub = new StubHttpHandler(request =>
        {
            string url = request.RequestUri?.AbsoluteUri ?? "";
            if (url.Contains("/v1/search", StringComparison.Ordinal)) return StubHttpHandler.Ok(WeatherClientTests.SampleBerlines);
            return url.Contains("/v1/forecast", StringComparison.Ordinal) ? StubHttpHandler.Ok(WeatherClientTests.SampleForecastLegacy) : StubHttpHandler.NotFound();
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

        // The defaulted property snapshot survives a change (the render-model
        // cache invalidation covers the property set — see IsCacheValid).
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
    public void Render_AllLayoutModes_ExecutesWithoutExceptions()
    {
        var widget = new WeatherForecastWidget();
        using var surface = SKSurface.Create(new SKImageInfo(400, 300));
        var canvas = surface.Canvas;
        var bounds = new SKRect(0, 0, 400, 300);

        string[] modes = ["Detailed", "Daily Forecast", "Hourly Forecast", "Current Only", "Compact"];
        foreach (var mode in modes)
        {
            canvas.Clear(SKColors.Black);
            widget.LayoutMode = mode;
            widget.Render(canvas, bounds);
            AssertWidgetDrewContent(surface, $"{mode} must paint content");
        }
    }

    [TestMethod]
    public void Render_SmallGridSizes_ExecutesWithoutExceptions()
    {
        var widget = new WeatherForecastWidget();
        using var surface = SKSurface.Create(new SKImageInfo(200, 160));
        var canvas = surface.Canvas;

        SKSize[] smallSizes = [new(200, 160), new(150, 120), new(120, 90)];
        foreach (var size in smallSizes)
        {
            canvas.Clear(SKColors.Black);
            widget.Render(canvas, new SKRect(0, 0, size.Width, size.Height));
        }

        AssertWidgetDrewContent(surface, "a small grid must still paint content");
    }

    private static void AssertWidgetDrewContent(SKSurface surface, string message)
    {
        using var image = surface.Snapshot();
        using var readback = SKBitmap.FromImage(image);
        Assert.IsTrue(readback.Pixels.Any(p => p.Alpha > 0 && (p.Red > 0 || p.Green > 0 || p.Blue > 0)),
            message);
    }
}
