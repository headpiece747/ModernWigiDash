namespace ModernWigiDash.Tests;

[TestClass]
public class WeatherRenderModelTests
{
    private static WeatherRenderModelKey MakeKey(
        int dataVersion = 1,
        SKRect? bounds = null,
        string layoutMode = "Standard",
        string unitSystem = "Celsius",
        string customLabel = "Label",
        string resolvedCity = "Berlin",
        bool showFeelsLike = true,
        bool showHumidity = true,
        bool showWind = true,
        bool showHighLow = true,
        bool showForecast = true,
        bool hideLocation = false,
        int candidateCount = 0,
        bool hasData = true)
        => new(dataVersion, bounds ?? new SKRect(0, 0, 400, 200), layoutMode, unitSystem, customLabel, resolvedCity,
            showFeelsLike, showHumidity, showWind, showHighLow, showForecast, hideLocation, candidateCount, hasData);

    /// <summary>The widget's cache-hit rule (EnsureRenderModel), pinned
    /// here verbatim in shape: the model must exist and its stored key must
    /// equal the freshly built key — a model that never went through the
    /// widget's build (null Key) can never be a cache hit.</summary>
    private static bool IsCacheHit(WeatherRenderModel model, WeatherRenderModelKey key)
        => model is { } cached && cached.Key == key;

    [TestMethod]
    public void Key_NoComponentDrift_RecordsAreEqual()
    {
        Assert.AreEqual(MakeKey(), MakeKey(), "identical components must be the same identity (a cache hit)");
    }

    [TestMethod]
    public void Key_DataVersionDrift_RecordsDiffer()
    {
        Assert.AreNotEqual(MakeKey(dataVersion: 1), MakeKey(dataVersion: 2),
            "a new snapshot must rebuild the model (the payload slices ride the data version)");
    }

    [TestMethod]
    public void Key_BoundsWidthDrift_RecordsDiffer()
    {
        Assert.AreNotEqual(MakeKey(), MakeKey(bounds: new SKRect(0, 0, 401, 200)),
            "a width change must rebuild (the layout derives font sizes from the bounds)");
    }

    [TestMethod]
    public void Key_BoundsHeightDrift_RecordsDiffer()
    {
        Assert.AreNotEqual(MakeKey(), MakeKey(bounds: new SKRect(0, 0, 400, 201)),
            "a height change must rebuild (the layout derives font sizes from the bounds)");
    }

    [TestMethod]
    public void Key_EachPropertySnapshotFieldDrift_RecordsDiffer()
    {
        // One concept: every property-snapshot field participates in the
        // identity — a silent drop of any one would let a stale formatted
        // string survive the property change that changes it.
        var baseline = MakeKey();

        Assert.AreNotEqual(baseline, MakeKey(layoutMode: "Compact"), "a layout-mode change must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(unitSystem: "Fahrenheit"), "a unit-system change must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(customLabel: "Home"), "a custom-label change must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(resolvedCity: "Paris"), "a resolved-city change must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(showFeelsLike: false), "a feels-like toggle must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(showHumidity: false), "a humidity toggle must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(showWind: false), "a wind toggle must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(showHighLow: false), "a high/low toggle must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(showForecast: false), "a forecast toggle must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(hideLocation: true), "a hide-location toggle must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(candidateCount: 3), "a candidate-count change must rebuild");
        Assert.AreNotEqual(baseline, MakeKey(hasData: false), "a data-to-no-data transition must rebuild (the no-data view has no strings of its own, so the flag is the identity's only signal)");
    }

    [TestMethod]
    public void IsCacheHit_NeverBuiltModel_NeverHits()
    {
        var model = new WeatherRenderModel(); // Key is null — never went through the build

        Assert.IsFalse(IsCacheHit(model, MakeKey()),
            "a model that never went through the widget's build (null Key) can never be a cache hit");
    }

    [TestMethod]
    public void IsCacheHit_IdentityMatches_Hits()
    {
        var key = MakeKey();
        var model = new WeatherRenderModel { Key = key };

        Assert.IsTrue(IsCacheHit(model, key), "the same identity must re-read the cached model, not rebuild");
    }

    [TestMethod]
    public void IsCacheHit_IdentityChanged_Misses()
    {
        var model = new WeatherRenderModel { Key = MakeKey(dataVersion: 1) };

        Assert.IsFalse(IsCacheHit(model, MakeKey(dataVersion: 2)),
            "any identity drift must force the rebuild");
    }
}
