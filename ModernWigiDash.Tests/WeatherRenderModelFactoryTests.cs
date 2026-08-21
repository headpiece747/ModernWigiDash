
namespace ModernWigiDash.Tests;

/// <summary>
/// The render-model build module pinned at its interface: the cache hit, the
/// key drift → rebuild rule, the key/data → model mapping, the display
/// composition (through the display rules, not re-derived), the header
/// truncation rule with the custom-label priority, and the pill-width
/// measurement. No widget instance, no render tick.
/// </summary>
[TestClass]
public class WeatherRenderModelFactoryTests
{
    private static readonly SKRect DesignBounds = new(0, 0, 406, 296);

    private static (WeatherHeaderLayout Header, float Scale) Geometry()
    {
        var (_, sy, s) = WeatherLayout.Scale(DesignBounds);
        return (WeatherLayout.ComputeHeader(DesignBounds, s, sy), s);
    }

    private static WeatherRenderModelKey Key(
            int dataVersion = 1,
            string? customLabel = null,
            string resolvedCity = "Berlin, Berlin, Germany",
            int candidateCount = 0,
            bool locationSet = false)
        => new(dataVersion, DesignBounds, WeatherLayout.DefaultLayoutMode,
            WeatherPresentation.DefaultUnitSystem, customLabel ?? "", resolvedCity,
            true, true, true, true, true, candidateCount, locationSet);

    private static WeatherRenderModelInputs Inputs(
        WeatherRenderModelKey key,
        int weatherCode = 61,
        bool isDay = true,
        double currentTempC = 21.5,
        double feelsLikeC = 19.0,
        double humidity = 60.0,
        double windSpeedKmH = 12.0,
        double highTempC = 24.0,
        double lowTempC = 17.0,
        string locationText = "Miami, Florida",
        int candidateCount = 0,
        IReadOnlyList<DailyForecastItem>? daily = null,
        IReadOnlyList<HourlyForecastItem>? hourly = null,
        string neutralLabel = "Default Location")
    {
        var (header, s) = Geometry();
        return new WeatherRenderModelInputs(
            key,
            weatherCode,
            isDay,
            currentTempC, feelsLikeC, humidity, windSpeedKmH, highTempC, lowTempC,
            daily ?? [new DailyForecastItem("Mon", 25.0, 15.0, 61)],
            hourly ?? [new HourlyForecastItem("13:00", 21.5, 61)],
            header, s,
            locationText, candidateCount, neutralLabel);
    }

    [TestMethod]
    public void Resolve_LocationEmptinessDrift_RebuildsAndFlipsTheSubtitleGuidance()
    {
        // No data yet (the unresolved verdict): the guidance line is chosen by the
        // location's emptiness — a composed string, so the key must cover it.
        var unset = Inputs(Key(resolvedCity: WeatherPresentation.UnknownLocationLabel, locationSet: false), locationText: "");
        var model1 = WeatherRenderModelFactory.Resolve(null, unset);
        Assert.AreEqual("Set a location in Settings", model1.SubtitleText);

        // The user types a location (the fetch is pending or failed — nothing
        // applied, so the data version never bumped): the guidance must flip
        // to the spelling hint, not stay frozen on the cache hit.
        var typed = Inputs(Key(resolvedCity: WeatherPresentation.UnknownLocationLabel, locationSet: true), locationText: "New Yrok");
        var model2 = WeatherRenderModelFactory.Resolve(model1, typed);

        Assert.AreNotSame(model1, model2,
            "a key hit must not reuse a model built for a different location emptiness");
        Assert.AreEqual("Check spelling \u2014 try 'City, State' or 'City, Country'", model2.SubtitleText,
            "the location's emptiness changes the composed subtitle — the key covers it");
    }

    [TestMethod]
    public void Resolve_CacheKeyMatches_ReturnsTheCachedInstance()
    {
        WeatherRenderModelInputs inputs = Inputs(Key());
        var cached = WeatherRenderModelFactory.Resolve(null, inputs);

        var result = WeatherRenderModelFactory.Resolve(cached, Inputs(cached.Key!));

        Assert.AreSame(cached, result,
            "A key hit must return the cached model — the per-frame path allocates nothing.");
    }

    [TestMethod]
    public void Resolve_WeatherCodeDriftWithoutDataVersion_IsACacheHit()
    {
        // The weather code is not a key component: it arrives with the data
        // version (the same apply bumps both), so a code-only drift can never
        // occur in production. The dependency is pinned here — a new data
        // field that changes WITHOUT the data version must be added to the
        // key, not left to the display's stale strings.
        WeatherRenderModelInputs inputs = Inputs(Key(), weatherCode: 0);
        var cached = WeatherRenderModelFactory.Resolve(null, inputs);

        var result = WeatherRenderModelFactory.Resolve(cached, Inputs(cached.Key!, weatherCode: 95));

        Assert.AreSame(cached, result,
            "The code rides the data version: a code-only drift is a hit, and the rebuild is driven by the version.");
    }

    [TestMethod]
    public void Resolve_IsDayDriftWithoutDataVersion_IsACacheHit()
    {
        // The day/night flag arrives with the weather code in the same apply
        // (the merge bumps the data version for both), so the flag — like the
        // code — rides the data version and is no key component.
        WeatherRenderModelInputs inputs = Inputs(Key(), isDay: false);
        var cached = WeatherRenderModelFactory.Resolve(null, inputs);

        var result = WeatherRenderModelFactory.Resolve(cached, Inputs(cached.Key!, isDay: true));

        Assert.AreSame(cached, result,
            "The flag rides the data version: an is_day-only drift is a hit, and the rebuild is driven by the version.");
    }

    [TestMethod]
    public void Resolve_EveryKeyFieldDrift_RebuildsTheModel()
    {
        WeatherRenderModelInputs inputs = Inputs(Key());
        var cached = WeatherRenderModelFactory.Resolve(null, inputs);
        WeatherRenderModelKey baseKey = inputs.Key;

        var driftedKeys = new[]
        {
            baseKey with { DataVersion = 2 },
            baseKey with { Bounds = new SKRect(0, 0, 407, 296) },
            baseKey with { LayoutMode = "Compact" },
            baseKey with { UnitSystem = "Celsius (°C, km/h)" },
            baseKey with { CustomLabel = "Home" },
            baseKey with { ResolvedCity = "Madrid, Madrid, Spain" },
            baseKey with { ShowFeelsLike = false },
            baseKey with { ShowHumidity = false },
            baseKey with { ShowWind = false },
            baseKey with { ShowHighLow = false },
            baseKey with { ShowForecast = false },
            baseKey with { CandidateCount = 5 },
        };

        foreach (WeatherRenderModelKey drifted in driftedKeys)
        {
            var result = WeatherRenderModelFactory.Resolve(cached, Inputs(drifted));
            Assert.AreNotSame(cached, result, $"A drift in {drifted} must force a rebuild.");
            Assert.AreEqual(drifted, result.Key, "The rebuilt model must carry the drifted key.");
        }
    }

    [TestMethod]
    public void Resolve_Miss_MapsTheKeyAndDataViewIntoTheModel()
    {
        WeatherRenderModelKey key = Key();
        var inputs = Inputs(key, weatherCode: 80, isDay: false);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        // The key rides by reference — the model's single identity; the
        // property snapshot the draw paths read comes from the key itself.
        Assert.AreSame(key, model.Key);
        Assert.AreEqual(80, model.WeatherCode);
        Assert.IsFalse(model.IsDay, "the day/night flag rides the data view into the model");
        Assert.AreEqual(1, model.Daily.Length);
        Assert.AreEqual(1, model.Hourly.Length);
        Assert.AreEqual(25.0, model.Daily[0].MaxTempC);
        Assert.AreEqual(15.0, model.Daily[0].MinTempC);
        Assert.AreEqual(61, model.Daily[0].WeatherCode);
        Assert.AreEqual("Mon", model.Daily[0].DayName);
        Assert.AreEqual(21.5, model.Hourly[0].TempC);
        Assert.AreEqual("13:00", model.Hourly[0].TimeLabel);
    }

    [TestMethod]
    public void Resolve_Miss_ComposesTheDisplayThroughTheDisplayRules()
    {
        // The factory must compose the display facts through WeatherPresentation
        // (one spelling), not re-derive them: for the same inputs the model's
        // display record is the presentation's record.
        var (tempUnit, speedUnit) = WeatherPresentation.ParseUnitSystem(WeatherPresentation.DefaultUnitSystem);
        WeatherRenderModelKey key = Key();
        var inputs = Inputs(key);
        var expected = WeatherPresentation.Build(new WeatherDisplayInput(
            inputs.CurrentTempC,
            new WeatherMetricsInput(
                key.ShowFeelsLike, inputs.FeelsLikeC,
                key.ShowHumidity, inputs.Humidity,
                key.ShowWind, inputs.WindSpeedKmH,
                key.ShowHighLow, inputs.HighTempC, inputs.LowTempC,
                tempUnit, speedUnit),
            inputs.Daily,
            inputs.Hourly));

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        // The display record's collection fields compare by reference, so a
        // whole-record AreEqual can never pass between two separately-built
        // instances — compare the facts field by field (content + order).
        Assert.AreEqual(expected.MainTemp, model.Display.MainTemp);
        CollectionAssert.AreEqual(expected.Metrics.ToList(), model.Display.Metrics.ToList());
        CollectionAssert.AreEqual(expected.ForecastRanges.ToList(), model.Display.ForecastRanges.ToList());
        CollectionAssert.AreEqual(expected.DailyHighLows.ToList(), model.Display.DailyHighLows.ToList());
        CollectionAssert.AreEqual(expected.HourlyTemps.ToList(), model.Display.HourlyTemps.ToList());
    }

    [TestMethod]
    public void Resolve_Miss_HeroTempFollowsTheKeyUnitSystem()
    {
        var celsius = WeatherRenderModelFactory.Resolve(null,
            Inputs(Key() with { UnitSystem = "Celsius (°C, km/h)" }, currentTempC: 25.0));
        var fahrenheit = WeatherRenderModelFactory.Resolve(null,
            Inputs(Key(), currentTempC: 25.0)); // the default unit system is Fahrenheit

        Assert.AreEqual("25.0°C", celsius.Display.MainTemp);
        Assert.AreEqual("77°F", fahrenheit.Display.MainTemp);
    }

    [TestMethod]
    public void Resolve_Miss_HeaderUsesTheCustomLabelWhenPresent()
    {
        var labeled = WeatherRenderModelFactory.Resolve(null, Inputs(Key(resolvedCity: "Paris, France", customLabel: "Home")));
        var unlabeled = WeatherRenderModelFactory.Resolve(null, Inputs(Key(resolvedCity: "Paris, France")));

        Assert.AreEqual("HOME", labeled.TruncatedHeader,
            "A short label that fits is uppercased verbatim; the custom label wins over the resolved city.");
        Assert.AreEqual("PARIS, FRANCE", unlabeled.TruncatedHeader,
            "Without a label, the resolved city is uppercased verbatim (a short city name fits).");
    }

    [TestMethod]
    public void Resolve_Miss_TruncatesAnOverlongCityName()
    {
        string overlong = new string('a', 400);
        var inputs = Inputs(Key(resolvedCity: overlong));

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.IsTrue(model.TruncatedHeader.Length < overlong.Length,
            "An overlong city name must be truncated to the header's max width.");
        Assert.IsTrue(model.TruncatedHeader.EndsWith('…'),
            "Truncation follows the shared ellipsis rule.");
    }

    [TestMethod]
    public void Resolve_Miss_MeasuresPillWidthsForEveryDisplayedMetric()
    {
        var model = WeatherRenderModelFactory.Resolve(null, Inputs(Key()));

        Assert.AreEqual(model.Display.Metrics.Count, model.MetricWidths.Length,
            "One cached width per displayed metric — the draw path's shrink re-measure shares this spelling.");
        foreach (float width in model.MetricWidths)
        {
            Assert.IsTrue(width > 0, "A measured pill width must be positive.");
        }
    }

    [TestMethod]
    public void Resolve_NullCache_ComposesAModelWithTheKeySet()
    {
        var inputs = Inputs(Key());

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.IsNotNull(model.Key,
            "A model built through the factory always carries its key — a null key (never built) can never hit.");
        Assert.AreEqual(inputs.Key, model.Key);
    }

    // --- Subtitle text tests ---

    [TestMethod]
    public void Resolve_TieState_CandidateCountGtZeroNoData_DrawsAmbiguityHint()
    {
        // Candidates exist but no weather data (tie) — the widget
        // shows "pick one in Settings" so the user knows what to do.
        var inputs = Inputs(Key(candidateCount: 3), candidateCount: 3, daily: [], hourly: []);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.AreEqual("Multiple cities found \u2014 pick one in Settings", model.SubtitleText,
            "A tie with no weather data must show the ambiguity hint.");
    }

    [TestMethod]
    public void Resolve_EmptyLocation_Unresolved_DrawsSetLocationHint()
    {
        // No location set yet — the widget tells the user where to go.
        var inputs = Inputs(Key(resolvedCity: WeatherPresentation.UnknownLocationLabel),
            locationText: "", candidateCount: 0, daily: [], hourly: []);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.AreEqual("Set a location in Settings", model.SubtitleText,
            "An empty location with no resolution must show the set-location hint.");
    }

    [TestMethod]
    public void Resolve_LocationSet_FailedResolution_DrawsCheckSpellingHint()
    {
        // User typed something but resolution failed — suggest the format.
        var inputs = Inputs(Key(resolvedCity: WeatherPresentation.UnknownLocationLabel, locationSet: true),
            locationText: "xyz123", candidateCount: 0, daily: [], hourly: []);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.AreEqual("Check spelling \u2014 try 'City, State' or 'City, Country'", model.SubtitleText,
            "A failed resolution with a non-empty location must show the spelling hint.");
    }

    [TestMethod]
    public void Resolve_BlankResolvedCityNoCustomLabel_HeaderFallsBackToInjectedNeutralLabel()
    {
        // A location edit drops the resolved name to blank (no resolution
        // yet) — the header must show the injected neutral label, not a blank
        // title, matching the fresh-widget seed: the same logical state
        // renders the same. A distinct label proves the factory reads the
        // injected value, not a hardcoded const.
        var inputs = Inputs(Key(resolvedCity: ""), neutralLabel: "Neutral Label");

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.AreEqual("NEUTRAL LABEL", model.TruncatedHeader,
            "a blank resolved city with no custom label shows the injected neutral label — not a blank header");
    }

    [TestMethod]
    public void Resolve_CustomLabelWithResolvedCity_DrawsResolvedCityConfirmation()
    {
        // User set a custom label — the widget shows the resolved city
        // so the user can confirm the widget resolved the right place.
        var inputs = Inputs(
            Key(resolvedCity: "Springfield, Massachusetts, United States", customLabel: "Home"),
            candidateCount: 0);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.AreEqual("Springfield, Massachusetts, United States", model.SubtitleText,
            "A custom label with a resolved city must show the resolved city for confirmation.");
    }

    [TestMethod]
    public void ResolveCustomLabelMatchesResolvedCity_NoSubtitle()
    {
        // When the custom label IS the resolved city, no confirmation is needed.
        var inputs = Inputs(
            Key(resolvedCity: "Paris, France", customLabel: "Paris, France"),
            candidateCount: 0);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.IsNull(model.SubtitleText,
            "When the custom label matches the resolved city, no subtitle is needed.");
    }

    [TestMethod]
    public void Resolve_ResolvedCityWithoutCustomLabel_NoSubtitle()
    {
        // Without a custom label, the header IS the resolved city — no
        // subtitle needed for confirmation.
        var inputs = Inputs(
            Key(resolvedCity: "Berlin, Berlin, Germany", customLabel: ""),
            candidateCount: 0);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.IsNull(model.SubtitleText,
            "Without a custom label, the resolved city needs no subtitle confirmation.");
    }

    [TestMethod]
    public void Resolve_TieTakesPrecedenceOverCustomLabel()
    {
        // Priority: a tie with candidates always shows the ambiguity
        // hint, even if a custom label is set.
        var inputs = Inputs(
            Key(resolvedCity: "Berlin", customLabel: "Home", candidateCount: 5),
            candidateCount: 5, daily: [], hourly: []);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.AreEqual("Multiple cities found \u2014 pick one in Settings", model.SubtitleText,
            "The tie hint must take precedence over the custom-label confirmation.");
    }

    [TestMethod]
    public void Resolve_FailedResolutionTakesPrecedenceOverCustomLabel()
    {
        // Priority: a failed resolution with a custom label still
        // shows the spelling guidance (the user typed something that didn't work).
        var inputs = Inputs(
            Key(resolvedCity: WeatherPresentation.UnknownLocationLabel, customLabel: "My Place"),
            locationText: "asdf", candidateCount: 0, daily: [], hourly: []);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.AreEqual("Check spelling \u2014 try 'City, State' or 'City, Country'", model.SubtitleText,
            "A failed resolution must take precedence over the custom-label confirmation.");
    }

    [TestMethod]
    public void Resolve_TieState_CandidateCountIsSetOnModel()
    {
        // The CandidateCount must ride the model's key — the key is the model's
        // single identity, and the widget's tie-state check reads it there.
        var inputs = Inputs(Key(candidateCount: 7), candidateCount: 7, daily: [], hourly: []);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.AreEqual(7, model.Key!.CandidateCount,
            "The key must carry the candidate count into the model.");
    }
}
