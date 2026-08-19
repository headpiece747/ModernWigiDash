using System.Text.Json;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public class WeatherForecastParserTests
{
    private static JsonElement RootOf(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static readonly JsonElement ModernRoot = RootOf(WeatherTestData.SampleForecast);
    private static readonly JsonElement LegacyRoot = RootOf(WeatherTestData.SampleForecastLegacy);

    [TestMethod]
    public void ParseCurrentWeather_ModernBlock_ParsesCurrentConditions()
    {
        var (tempC, feelsLikeC, windSpeedKmH, weatherCode, isDay) = WeatherForecastParser.ParseCurrentWeather(ModernRoot);

        Assert.AreEqual(12.5, tempC);
        Assert.AreEqual(10.1, feelsLikeC);
        Assert.AreEqual(8.2, windSpeedKmH);
        Assert.AreEqual(2, weatherCode);
        Assert.IsNull(isDay, "the shared fixture carries no is_day — an absent flag reads as unknown");
    }

    [TestMethod]
    public void ParseCurrentWeather_LegacyBlock_FallsBackWithNoFeelsLike()
    {
        var (tempC, feelsLikeC, windSpeedKmH, weatherCode, isDay) = WeatherForecastParser.ParseCurrentWeather(LegacyRoot);

        Assert.AreEqual(12.5, tempC);
        Assert.IsNull(feelsLikeC, "the legacy block has no apparent_temperature — feels-like stays null");
        Assert.AreEqual(8.2, windSpeedKmH);
        Assert.AreEqual(2, weatherCode);
        Assert.IsNull(isDay, "the legacy fixture carries no is_day — an absent flag reads as unknown");
    }

    [TestMethod]
    public void ParseCurrentWeather_NoBlocks_ReturnsNulls()
    {
        var (tempC, feelsLikeC, windSpeedKmH, weatherCode, isDay) = WeatherForecastParser.ParseCurrentWeather(RootOf("{}"));

        Assert.IsNull(tempC);
        Assert.IsNull(feelsLikeC);
        Assert.IsNull(windSpeedKmH);
        Assert.IsNull(weatherCode);
        Assert.IsNull(isDay);
    }

    [TestMethod]
    public void ParseCurrentWeather_ModernBlock_IsDayZero_ReadsNight()
    {
        string json = """{ "current": { "weather_code": 0, "is_day": 0 } }""";

        var (tempC, _, _, weatherCode, isDay) = WeatherForecastParser.ParseCurrentWeather(RootOf(json));

        Assert.IsNull(tempC);
        Assert.AreEqual(0, weatherCode);
        Assert.IsFalse(isDay, "is_day 0 is night — the current-condition icon flips to a moon");
    }

    [TestMethod]
    public void ParseCurrentWeather_ModernBlock_IsDayOne_ReadsDay()
    {
        string json = """{ "current": { "weather_code": 0, "is_day": 1 } }""";

        var (_, _, _, _, isDay) = WeatherForecastParser.ParseCurrentWeather(RootOf(json));

        Assert.IsTrue(isDay, "is_day 1 is day");
    }

    [TestMethod]
    public void ParseCurrentWeather_LegacyBlock_IsDayZero_ReadsNight()
    {
        string json = """{ "current_weather": { "weathercode": 2, "is_day": 0 } }""";

        var (_, _, _, weatherCode, isDay) = WeatherForecastParser.ParseCurrentWeather(RootOf(json));

        Assert.AreEqual(2, weatherCode);
        Assert.IsFalse(isDay, "the legacy current_weather block carries is_day too");
    }

    [TestMethod]
    public void ParseCurrentWeather_LegacyBlock_IsDayOne_ReadsDay()
    {
        string json = """{ "current_weather": { "weathercode": 0, "is_day": 1 } }""";

        var (_, _, _, _, isDay) = WeatherForecastParser.ParseCurrentWeather(RootOf(json));

        Assert.IsTrue(isDay, "the legacy current_weather block reads is_day 1 as day");
    }

    [TestMethod]
    public void ParseHourlyForecast_ModernHumidity_ComesFromCurrentBlock()
    {
        var (humidity, hourly) = WeatherForecastParser.ParseHourlyForecast(ModernRoot);

        Assert.AreEqual(60, humidity, "modern humidity is the current block's 15-minute value");
        Assert.IsNotNull(hourly);
        Assert.AreEqual(2, hourly.Count);
        Assert.AreEqual("00:00", hourly[0].TimeLabel);
        Assert.AreEqual(12.5, hourly[0].TempC);
        Assert.AreEqual(2, hourly[0].WeatherCode);
        Assert.AreEqual("01:00", hourly[1].TimeLabel);
        Assert.AreEqual(13.1, hourly[1].TempC);
    }

    [TestMethod]
    public void ParseHourlyForecast_LegacyHumidity_ComesFromHourlyArray()
    {
        var (humidity, hourly) = WeatherForecastParser.ParseHourlyForecast(LegacyRoot);

        Assert.AreEqual(60, humidity, "legacy humidity is the hourly array's first bucket (the only source)");
        Assert.IsNotNull(hourly);
        Assert.AreEqual("12:00", hourly[0].TimeLabel);
        Assert.AreEqual("13:00", hourly[1].TimeLabel);
        Assert.AreEqual(13.1, hourly[1].TempC);
    }

    [TestMethod]
    public void ParseHourlyForecast_NoHourly_ReturnsHumidityWithNullStrip()
    {
        string json = """{ "current": { "relative_humidity_2m": 55 } }""";

        var (humidity, hourly) = WeatherForecastParser.ParseHourlyForecast(RootOf(json));

        Assert.AreEqual(55, humidity);
        Assert.IsNull(hourly);
    }

    [TestMethod]
    public void ParseHourlyForecast_NoData_ReturnsNulls()
    {
        var (humidity, hourly) = WeatherForecastParser.ParseHourlyForecast(RootOf("{}"));

        Assert.IsNull(humidity);
        Assert.IsNull(hourly);
    }

    [TestMethod]
    public void ParseDailyForecast_ModernShape_ParsesHighLowAndStrip()
    {
        var (highTempC, lowTempC, daily) = WeatherForecastParser.ParseDailyForecast(ModernRoot);

        Assert.AreEqual(18.0, highTempC);
        Assert.AreEqual(9.0, lowTempC);
        Assert.IsNotNull(daily);
        Assert.AreEqual(2, daily.Count);
        Assert.AreEqual("Today", daily[0].DayName);
        Assert.AreEqual(18.0, daily[0].MaxTempC);
        Assert.AreEqual(9.0, daily[0].MinTempC);
        Assert.AreEqual(2, daily[0].WeatherCode);
        Assert.AreEqual("Saturday", daily[1].DayName, "2026-08-08 is a Saturday — full invariant day name");
        Assert.AreEqual(20.0, daily[1].MaxTempC);
        Assert.AreEqual(3, daily[1].WeatherCode);
    }

    [TestMethod]
    public void ParseDailyForecast_LegacyShape_ParsesWithLegacyCodes()
    {
        var (highTempC, lowTempC, daily) = WeatherForecastParser.ParseDailyForecast(LegacyRoot);

        Assert.AreEqual(18.0, highTempC);
        Assert.AreEqual(9.0, lowTempC);
        Assert.IsNotNull(daily);
        Assert.AreEqual("Today", daily[0].DayName);
        Assert.AreEqual(2, daily[0].WeatherCode);
    }

    [TestMethod]
    public void ParseDailyForecast_NoDaily_ReturnsNulls()
    {
        var (highTempC, lowTempC, daily) = WeatherForecastParser.ParseDailyForecast(RootOf("{}"));

        Assert.IsNull(highTempC);
        Assert.IsNull(lowTempC);
        Assert.IsNull(daily);
    }

    [TestMethod]
    public void ParseHourlyForecast_OverlongArrays_CapAtFetchLimits()
    {
        string times = string.Join(",", Enumerable.Range(0, 30).Select(i => $"\"2026-08-07T{i:D2}:00\""));
        string temps = string.Join(",", Enumerable.Range(0, 30).Select(i => $"{10.0 + i}"));
        string codes = string.Join(",", Enumerable.Range(0, 30).Select(_ => "1"));
        var root = RootOf(
            "{\"hourly\":{\"time\":[" + times + "],\"temperature_2m\":[" + temps + "],\"weather_code\":[" + codes + "]}}");

        var (humidity, hourly) = WeatherForecastParser.ParseHourlyForecast(root);

        Assert.IsNull(humidity);
        Assert.AreEqual(WeatherForecastLimits.MaxFetchHours, hourly!.Count,
            "the hourly strip must cap at the fetch limit (MaxFetchHours)");
    }

    [TestMethod]
    public void ParseDailyForecast_OverlongArrays_CapAtFetchLimits()
    {
        string dates = string.Join(",", Enumerable.Range(0, 20).Select(i => $"\"2026-08-{(7 + i):D2}\""));
        string maxes = string.Join(",", Enumerable.Range(0, 20).Select(i => $"{10.0 + i}"));
        string mins = string.Join(",", Enumerable.Range(0, 20).Select(i => $"{0.0 + i}"));
        string codes = string.Join(",", Enumerable.Range(0, 20).Select(_ => "1"));
        var root = RootOf(
            "{\"daily\":{\"time\":[" + dates + "],\"weather_code\":[" + codes + "],\"temperature_2m_max\":[" + maxes + "],\"temperature_2m_min\":[" + mins + "]}}");

        var (_, _, daily) = WeatherForecastParser.ParseDailyForecast(root);

        Assert.AreEqual(WeatherForecastLimits.MaxFetchDays, daily!.Count,
            "the daily strip must cap at the fetch limit (MaxFetchDays)");
    }

    [TestMethod]
    public void ParseDailyForecast_UnparseableDate_FallsBackToIndexName()
    {
        string json = """{"daily":{"time":["not-a-date"],"weather_code":[1],"temperature_2m_max":[18.0],"temperature_2m_min":[9.0]}}""";

        var (_, _, daily) = WeatherForecastParser.ParseDailyForecast(RootOf(json));

        Assert.IsNotNull(daily);
        Assert.AreEqual("Today", daily[0].DayName, "the first strip row is always 'Today'");
    }

    [TestMethod]
    public void ParseHourlyForecast_ShortTimeStrings_FallBackToIndexLabels()
    {
        string json = """{"hourly":{"time":["12"],"temperature_2m":[12.5],"weather_code":[2]}}""";

        var (_, hourly) = WeatherForecastParser.ParseHourlyForecast(RootOf(json));

        Assert.IsNotNull(hourly);
        Assert.AreEqual("0:00", hourly[0].TimeLabel, "a time string too short for [11..16] slices to an index label");
    }
}
