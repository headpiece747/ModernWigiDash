using System.Globalization;
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
    public void FormatTempAndSpeed_AreInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            // A comma-decimal locale must not render "21,5°C" on the display.
            Assert.AreEqual("21.5°C", WeatherPresentation.FormatTemp(21.5, "°C"));
            Assert.AreEqual("22°F", WeatherPresentation.FormatTemp(-5.5, "°F"));
            Assert.AreEqual("12 km/h", WeatherPresentation.FormatSpeed(12.0, "km/h"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
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
    public void MapWmoCode_FullSweep_EveryCode0To99MapsToNonEmpty()
    {
        // The full WMO range sweep: every code 0..99 must map to a non-empty
        // icon + description, the known group codes must map to their pinned
        // group, and every code outside the table must read the fair fallback
        // (a code accidentally dropped from a group would silently mislabel
        // the forecast strip).
        var pinned = new Dictionary<int, (string Icon, string Description)>
        {
            [0] = ("☀️", "Clear Sky"),
            [1] = ("🌤️", "Mainly Clear"),
            [2] = ("⛅", "Partly Cloudy"),
            [3] = ("☁️", "Overcast"),
            [45] = ("🌫️", "Foggy"),
            [48] = ("🌫️", "Foggy"),
            [51] = ("🌧️", "Drizzle"),
            [53] = ("🌧️", "Drizzle"),
            [55] = ("🌧️", "Drizzle"),
            [56] = ("🌧️❄️", "Freezing Drizzle"),
            [57] = ("🌧️❄️", "Freezing Drizzle"),
            [61] = ("🌧️", "Rainy"),
            [63] = ("🌧️", "Rainy"),
            [65] = ("🌧️", "Rainy"),
            [66] = ("🌧️❄️", "Freezing Rain"),
            [67] = ("🌧️❄️", "Freezing Rain"),
            [71] = ("❄️", "Snowy"),
            [73] = ("❄️", "Snowy"),
            [75] = ("❄️", "Snowy"),
            [77] = ("❄️", "Snowy"),
            [80] = ("🌦️", "Rain Showers"),
            [81] = ("🌦️", "Rain Showers"),
            [82] = ("🌦️", "Rain Showers"),
            [85] = ("🌨️", "Snow Showers"),
            [86] = ("🌨️", "Snow Showers"),
            [95] = ("🌩️", "Thunderstorm"),
            [96] = ("🌩️", "Thunderstorm"),
            [99] = ("🌩️", "Thunderstorm"),
        };

        for (int code = 0; code <= 99; code++)
        {
            var (icon, desc) = WeatherPresentation.MapWmoCode(code);
            Assert.IsFalse(string.IsNullOrEmpty(icon), $"code {code} must map to an icon");
            Assert.IsFalse(string.IsNullOrEmpty(desc), $"code {code} must map to a description");
            if (pinned.TryGetValue(code, out var want))
            {
                Assert.AreEqual(want, (icon, desc), $"code {code} must map to its pinned group");
            }
            else
            {
                Assert.AreEqual(("☀️", "Fair"), (icon, desc), $"code {code} (outside the WMO table) must read fair");
            }
        }
    }

    [TestMethod]
    public void MapWmoIcon_Day_MatchesTheDayIconSet()
    {
        Assert.AreEqual(WeatherPresentation.MapWmoCode(0).Icon, WeatherPresentation.MapWmoIcon(0, true));
        Assert.AreEqual(WeatherPresentation.MapWmoCode(61).Icon, WeatherPresentation.MapWmoIcon(61, true));
        Assert.AreEqual(WeatherPresentation.MapWmoCode(95).Icon, WeatherPresentation.MapWmoIcon(95, true));
    }

    [TestMethod]
    public void MapWmoIcon_Night_ClearSkiesReadAsAMoon()
    {
        Assert.AreEqual("🌙", WeatherPresentation.MapWmoIcon(0, false), "clear at night shows the moon");
        Assert.AreEqual("🌙", WeatherPresentation.MapWmoIcon(1, false), "mainly clear at night shows the moon");
        Assert.AreEqual("🌃", WeatherPresentation.MapWmoIcon(2, false), "partly cloudy at night shows the night city");
    }

    [TestMethod]
    public void MapWmoIcon_Night_PrecipitationKeepsItsDayIcon()
    {
        Assert.AreEqual(WeatherPresentation.MapWmoCode(61).Icon, WeatherPresentation.MapWmoIcon(61, false));
        Assert.AreEqual(WeatherPresentation.MapWmoCode(77).Icon, WeatherPresentation.MapWmoIcon(77, false));
    }

    [TestMethod]
    public void MapWmoIcon_Night_FullSweep_EveryCode0To99MapsToNonEmpty()
    {
        // The night table's fall-through is the load-bearing arm: every code
        // without a night override must still map to a non-empty icon (its
        // day icon) — a table edit that blanked a night icon would silently
        // draw nothing on the display.
        for (int code = 0; code <= 99; code++)
        {
            string icon = WeatherPresentation.MapWmoIcon(code, isDay: false);
            Assert.IsFalse(string.IsNullOrEmpty(icon), $"night code {code} must map to a non-empty icon");
        }
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

    [TestMethod]
    public void Build_CapsDailyAtFiveAndHourlyAtSix()
    {
        var daily = Enumerable.Range(0, 8)
            .Select(i => new DailyForecastItem($"Day{i}", 20 + i, 10 + i, 1)).ToArray();
        var hourly = Enumerable.Range(0, 10)
            .Select(i => new HourlyForecastItem($"{i}:00", 15 + i, 1)).ToArray();
        var input = new WeatherDisplayInput(22.5, new WeatherMetricsInput(false, 0, false, 0, false, 0, false, 0, 0, "°C", "km/h"), daily, hourly);

        var display = WeatherPresentation.Build(input);

        Assert.AreEqual(5, display.ForecastRanges.Count, "the daily strip caps at five days");
        Assert.AreEqual(5, display.DailyHighLows.Count, "the high/low rows follow the same cap");
        Assert.AreEqual(6, display.HourlyTemps.Count, "the hourly strip caps at six hours");
        Assert.AreEqual("22.5°C", display.MainTemp);
        // The cap keeps the FIRST entries, never the last: the ranges/high-lows
        // must carry Day0 (20/10), not Day7 (27/17), and the hourly strip must
        // carry hour 0 (15°C), not hour 9.
        Assert.AreEqual("20° / 10°", display.ForecastRanges[0],
            "the cap must keep the first day's range, not the last");
        Assert.AreEqual("High: 20.0°C  Low: 10.0°C", display.DailyHighLows[0],
            "the high/low rows must keep the first day too");
        Assert.AreEqual("15.0°C", display.HourlyTemps[0],
            "the hourly strip must keep the first hour, not the last");
    }

    [TestMethod]
    public void Build_ShortListsArePassedThroughUncapped()
    {
        var daily = new[] { new DailyForecastItem("Day0", 20, 10, 1) };
        var hourly = new[] { new HourlyForecastItem("0:00", 15, 1) };
        var input = new WeatherDisplayInput(22.5, new WeatherMetricsInput(false, 0, false, 0, false, 0, false, 0, 0, "°C", "km/h"), daily, hourly);

        var display = WeatherPresentation.Build(input);

        Assert.AreEqual(1, display.ForecastRanges.Count);
        Assert.AreEqual(1, display.HourlyTemps.Count);
    }
}
