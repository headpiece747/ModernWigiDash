using System.Globalization;

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
    public void MapWmoIcon_Day_MatchesTheDayRow()
    {
        // Day: the whole display fact is exactly the WMO row (icon + text) —
        // the night flip must never leak into the day path.
        Assert.AreEqual(WeatherPresentation.MapWmoCode(0), WeatherPresentation.MapWmoIcon(0, true));
        Assert.AreEqual(WeatherPresentation.MapWmoCode(61), WeatherPresentation.MapWmoIcon(61, true));
        Assert.AreEqual(WeatherPresentation.MapWmoCode(95), WeatherPresentation.MapWmoIcon(95, true));
    }

    [TestMethod]
    public void MapWmoIcon_Night_ClearSkiesReadAsAMoon()
    {
        // The moon/night-city icon rides the DAY-NEUTRAL description: "Clear
        // Sky" stays the text at night — only the icon flips.
        Assert.AreEqual(("🌙", "Clear Sky"), WeatherPresentation.MapWmoIcon(0, false),
            "clear at night shows the moon with the day-neutral text");
        Assert.AreEqual(("🌙", "Mainly Clear"), WeatherPresentation.MapWmoIcon(1, false),
            "mainly clear at night shows the moon with the day-neutral text");
        Assert.AreEqual(("🌃", "Partly Cloudy"), WeatherPresentation.MapWmoIcon(2, false),
            "partly cloudy at night shows the night city with the day-neutral text");
    }

    [TestMethod]
    public void MapWmoIcon_Night_PrecipitationKeepsItsDayRow()
    {
        // Every code without a night override keeps its FULL day row (icon and
        // text) — precipitation renders the same all day.
        Assert.AreEqual(WeatherPresentation.MapWmoCode(61), WeatherPresentation.MapWmoIcon(61, false));
        Assert.AreEqual(WeatherPresentation.MapWmoCode(77), WeatherPresentation.MapWmoIcon(77, false));
    }

    [TestMethod]
    public void MapWmoIcon_Night_FullSweep_EveryCode0To99MapsToNonEmpty()
    {
        // The night table's fall-through is the load-bearing arm: every code
        // without a night override must still map to a non-empty icon (its
        // day icon) — a table edit that blanked a night icon would silently
        // draw nothing on the display. The description must pass through the
        // WMO row's text unchanged (day-neutral) for every code.
        for (int code = 0; code <= 99; code++)
        {
            var (icon, desc) = WeatherPresentation.MapWmoIcon(code, isDay: false);
            Assert.IsFalse(string.IsNullOrEmpty(icon), $"night code {code} must map to a non-empty icon");
            Assert.AreEqual(WeatherPresentation.MapWmoCode(code).Description, desc,
                $"night code {code} must keep the day-neutral description");
            if (code is not 0 and not 1 and not 2)
            {
                // The fall-through arm pinned per code: a night override that
                // leaks onto a non-clear code (fog, snow, ...) would pass the
                // non-empty and description checks above, so every other code
                // must read exactly its day icon.
                Assert.AreEqual(WeatherPresentation.MapWmoCode(code).Icon, icon,
                    $"night code {code} must keep its day icon (no night override)");
            }
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

    // --- BuildSubtitle: the subtitle guidance line ---------------------------

    [TestMethod]
    public void BuildSubtitle_TieWithNoData_PromptToPick()
    {
        Assert.AreEqual("Multiple cities found \u2014 pick one in Settings",
            WeatherPresentation.BuildSubtitle(WeatherPresentation.UnknownLocationLabel, "Victoria", 2, 0));
    }

    [TestMethod]
    public void BuildSubtitle_TieWithResolvedCity_StillTheAmbiguityPrompt()
    {
        Assert.AreEqual("Multiple cities found \u2014 pick one in Settings",
            WeatherPresentation.BuildSubtitle("Victoria", "Victoria", 3, 0),
            "the most actionable message wins — a tie beats every other prompt");
    }

    [TestMethod]
    public void BuildSubtitle_NoLocation_NoLocationPrompt()
    {
        Assert.AreEqual("Set a location in Settings",
            WeatherPresentation.BuildSubtitle(WeatherPresentation.UnknownLocationLabel, "", 0, 0));
    }

    [TestMethod]
    public void BuildSubtitle_LocationSetButUnresolved_SpellingPrompt()
    {
        Assert.AreEqual("Check spelling \u2014 try 'City, State' or 'City, Country'",
            WeatherPresentation.BuildSubtitle(WeatherPresentation.UnknownLocationLabel, "Xyzzyville", 0, 0));
    }

    [TestMethod]
    public void BuildSubtitle_ResolvedCityWithCustomLabel_NoConfirmationSubtitle()
    {
        // The resolved city no longer echoes under a custom label: the
        // confirmation line was the "still shows underneath" complaint.
        Assert.IsNull(WeatherPresentation.BuildSubtitle("Berlin, Germany", "Berlin, Germany", 0, 5));
    }

    [TestMethod]
    public void BuildSubtitle_ResolvedEverything_NoSubtitle()
    {
        Assert.IsNull(WeatherPresentation.BuildSubtitle("Berlin, Germany", "Berlin, Germany", 1, 5));
    }

    // --- BuildStalenessText: the header staleness line ------------------------

    [TestMethod]
    public void BuildStalenessText_FetchingInProgress_Updating()
    {
        var now = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual("Updating\u2026", WeatherPresentation.BuildStalenessText(true, now.AddHours(-1), now));
    }

    [TestMethod]
    public void BuildStalenessText_NeverFetched_NoText()
    {
        Assert.IsNull(WeatherPresentation.BuildStalenessText(false, DateTime.MinValue, DateTime.UtcNow));
    }

    [TestMethod]
    public void BuildStalenessText_SinceLastFetch_TimeAgo()
    {
        var now = new DateTime(2026, 3, 4, 12, 0, 0, DateTimeKind.Utc);
        Assert.AreEqual("Updated 30m ago", WeatherPresentation.BuildStalenessText(false, now.AddMinutes(-30), now));
    }

    [TestMethod]
    public void FormatTimeAgo_AllBranches_FormatsTheBuckets()
    {
        // Each threshold and unit suffix is a distinct display fact — pin all
        // four buckets so a regression in any threshold or suffix is caught.
        Assert.AreEqual("Updated just now", WeatherPresentation.FormatTimeAgo(TimeSpan.FromSeconds(30)));
        Assert.AreEqual("Updated 1m ago", WeatherPresentation.FormatTimeAgo(TimeSpan.FromMinutes(1)));
        Assert.AreEqual("Updated 59m ago", WeatherPresentation.FormatTimeAgo(TimeSpan.FromMinutes(59)));
        Assert.AreEqual("Updated 2h ago", WeatherPresentation.FormatTimeAgo(TimeSpan.FromHours(2)));
        Assert.AreEqual("Updated 23h ago", WeatherPresentation.FormatTimeAgo(TimeSpan.FromHours(23)));
        Assert.AreEqual("Updated 1d ago", WeatherPresentation.FormatTimeAgo(TimeSpan.FromDays(1)));
        Assert.AreEqual("Updated 5d ago", WeatherPresentation.FormatTimeAgo(TimeSpan.FromDays(5)));
    }

    [TestMethod]
    public void NoDataDisplay_TheGlyphAndNoDisplayStrings()
    {
        var display = WeatherPresentation.NoDataDisplay();

        Assert.AreEqual("—", display.MainTemp,
            "the hero temperature is the em dash glyph — the FrameTimePresentation no-reading precedent, never a placeholder scalar");
        Assert.AreEqual(0, display.Metrics.Count, "no pills: the placeholder scalars must not compose as display strings");
        Assert.AreEqual(0, display.ForecastRanges.Count);
        Assert.AreEqual(0, display.DailyHighLows.Count);
        Assert.AreEqual(0, display.HourlyTemps.Count);
    }
}
