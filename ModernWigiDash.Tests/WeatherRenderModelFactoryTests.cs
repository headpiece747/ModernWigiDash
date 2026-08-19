using ModernWigiDash.Widgets;
using SkiaSharp;

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
        string resolvedCity = "Berlin, Berlin, Germany")
        => new(dataVersion, DesignBounds, WeatherLayout.DefaultLayoutMode,
            WeatherPresentation.DefaultUnitSystem, customLabel ?? "", resolvedCity,
            true, true, true, true, true);

    private static WeatherRenderModelInputs Inputs(
        WeatherRenderModelKey key,
        int weatherCode = 61,
        double currentTempC = 21.5,
        double feelsLikeC = 19.0,
        double humidity = 60.0,
        double windSpeedKmH = 12.0,
        double highTempC = 24.0,
        double lowTempC = 17.0)
    {
        var (header, s) = Geometry();
        return new WeatherRenderModelInputs(
            key,
            weatherCode,
            currentTempC, feelsLikeC, humidity, windSpeedKmH, highTempC, lowTempC,
            [new DailyForecastItem("Mon", 25.0, 15.0, 61)],
            [new HourlyForecastItem("13:00", 21.5, 61)],
            header, s);
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
    public void Resolve_EveryKeyFieldDrift_RebuildsTheModel()
    {
        WeatherRenderModelInputs inputs = Inputs(Key());
        var cached = WeatherRenderModelFactory.Resolve(null, inputs);
        WeatherRenderModelKey baseKey = inputs.Key;

        var driftedKeys = new []
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
        var inputs = Inputs(key, weatherCode: 80);

        var model = WeatherRenderModelFactory.Resolve(null, inputs);

        Assert.AreEqual(key, model.Key);
        Assert.AreEqual(key.DataVersion, model.DataVersion);
        Assert.AreEqual(key.Bounds, model.Bounds);
        Assert.AreEqual(key.LayoutMode, model.LayoutMode);
        Assert.AreEqual(key.UnitSystem, model.UnitSystem);
        Assert.AreEqual(key.CustomLabel, model.CustomLabel);
        Assert.AreEqual(key.ResolvedCity, model.ResolvedCity);
        Assert.AreEqual(key.ShowFeelsLike, model.ShowFeelsLike);
        Assert.AreEqual(key.ShowHumidity, model.ShowHumidity);
        Assert.AreEqual(key.ShowWind, model.ShowWind);
        Assert.AreEqual(key.ShowHighLow, model.ShowHighLow);
        Assert.AreEqual(key.ShowForecast, model.ShowForecast);
        Assert.AreEqual(80, model.WeatherCode);
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
}