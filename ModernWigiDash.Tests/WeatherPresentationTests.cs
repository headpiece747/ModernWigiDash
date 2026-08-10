using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Weather widget's display rules: unit conversions, the WMO condition
/// table, and the composed pill/row strings the five layout modes draw.
/// Previously inline in the render paths (or parked in the data module) and
/// never asserted.
/// </summary>
[TestClass]
public class WeatherPresentationTests
{
    [TestMethod]
    public void FormatTemp_Celsius_KelvinAndFahrenheit()
    {
        Assert.AreEqual("23.4°C", WeatherPresentation.FormatTemp(23.4, "°C"));
        Assert.AreEqual("23°", WeatherPresentation.FormatTemp(23.4, "°C", shortFormat: true));
        Assert.AreEqual("32°F", WeatherPresentation.FormatTemp(0, "°F"));
        Assert.AreEqual("32°", WeatherPresentation.FormatTemp(0, "°F", shortFormat: true));
        Assert.AreEqual("77°F", WeatherPresentation.FormatTemp(25, "°F"));
        Assert.AreEqual("273 K", WeatherPresentation.FormatTemp(0, "K"));
    }

    [TestMethod]
    public void FormatSpeed_KmhConvertsPerUnit()
    {
        Assert.AreEqual("12 km/h", WeatherPresentation.FormatSpeed(12.0, "km/h"));
        Assert.AreEqual("7 mph", WeatherPresentation.FormatSpeed(12.0, "mph"), "12 km/h * 0.621371 rounds to 7");
        Assert.AreEqual("3 m/s", WeatherPresentation.FormatSpeed(12.0, "m/s"), "12 / 3.6 rounds to 3");
    }

    [TestMethod]
    public void ParseUnitSystem_EveryChoice_MapsToTokens()
    {
        Assert.AreEqual(("°F", "mph"), WeatherPresentation.ParseUnitSystem("Fahrenheit (°F, mph)"));
        Assert.AreEqual(("°C", "km/h"), WeatherPresentation.ParseUnitSystem("Celsius (°C, km/h)"));
        Assert.AreEqual(("°C", "mph"), WeatherPresentation.ParseUnitSystem("Celsius (°C, mph)"));
        Assert.AreEqual(("°C", "m/s"), WeatherPresentation.ParseUnitSystem("Celsius (°C, m/s)"));
        Assert.AreEqual(("K", "m/s"), WeatherPresentation.ParseUnitSystem("Kelvin (K, m/s)"));
        Assert.AreEqual(("°C", "km/h"), WeatherPresentation.ParseUnitSystem(""));
        Assert.AreEqual(("°C", "km/h"), WeatherPresentation.ParseUnitSystem("Bogus"));
    }

    [TestMethod]
    public void MapWmoCode_RepresentativeCodes()
    {
        Assert.AreEqual(("☀️", "Clear Sky"), WeatherPresentation.MapWmoCode(0));
        Assert.AreEqual(("⛅", "Partly Cloudy"), WeatherPresentation.MapWmoCode(2));
        Assert.AreEqual(("🌧️", "Rainy"), WeatherPresentation.MapWmoCode(61));
        Assert.AreEqual(("❄️", "Snowy"), WeatherPresentation.MapWmoCode(77));
        Assert.AreEqual(("🌩️", "Thunderstorm"), WeatherPresentation.MapWmoCode(95));
        Assert.AreEqual(("☀️", "Fair"), WeatherPresentation.MapWmoCode(999), "unknown codes read fair");
    }

    [TestMethod]
    public void MetricPills_OnlyEnabledPillsInFixedOrder()
    {
        var input = new WeatherMetricsInput(
            ShowFeelsLike: true, FeelsLikeC: 22.0,
            ShowHumidity: false, Humidity: 45,
            ShowWind: true, WindKmh: 12.0,
            ShowHighLow: true, HighC: 25, LowC: 16,
            TempUnit: "°C", SpeedUnit: "km/h");

        var pills = WeatherPresentation.MetricPills(input);

        CollectionAssert.AreEqual(new[] { "Feels: 22°", "Wind: 12 km/h", "H:25° L:16°" }, pills.ToArray());
    }

    [TestMethod]
    public void MetricPills_AllDisabled_Empty()
    {
        var pills = WeatherPresentation.MetricPills(new WeatherMetricsInput(
            false, 0, false, 0, false, 0, false, 0, 0, "°C", "km/h"));

        Assert.AreEqual(0, pills.Count);
    }

    [TestMethod]
    public void ForecastRangeText_ShortUnitsJoined()
    {
        Assert.AreEqual("25° / 16°", WeatherPresentation.ForecastRangeText(25, 16, "°C"));
        Assert.AreEqual("77° / 61°", WeatherPresentation.ForecastRangeText(25, 16, "°F"));
    }

    [TestMethod]
    public void ToggleUnitSystem_CyclesFahrenheitAndCelsius()
    {
        Assert.AreEqual("Celsius (°C, km/h)", WeatherPresentation.ToggleUnitSystem("Fahrenheit (°F, mph)"));
        Assert.AreEqual("Fahrenheit (°F, mph)", WeatherPresentation.ToggleUnitSystem("Celsius (°C, km/h)"));
        Assert.AreEqual(WeatherPresentation.DefaultUnitSystem, WeatherPresentation.ToggleUnitSystem("Kelvin (K, m/s)"),
            "an unknown system falls back to the default on toggle");
    }

    [TestMethod]
    public void DailyHighLowText_LongUnitsJoined()
    {
        Assert.AreEqual("High: 25.0°C  Low: 16.0°C", WeatherPresentation.DailyHighLowText(25, 16, "°C"));
        Assert.AreEqual("High: 77°F  Low: 61°F", WeatherPresentation.DailyHighLowText(25, 16, "°F"));
    }
}
