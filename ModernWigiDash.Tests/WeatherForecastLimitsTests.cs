using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the weather cluster's row-cap module: the fetch-tier caps the data
/// module keeps (7 daily / 12 hourly) and the draw-tier caps the renderer
/// can paint (5 / 6). The invariant the module guarantees, draw ≤ fetch, is
/// pinned at runtime through the display model: fed the MAXIMUM rows the
/// fetch tier can provide, the strip columns must still come out fully
/// populated. If the draw tier ever exceeded the fetch tier, this test's
/// display would come up short — the caps are asserted by behavior, not by
/// re-stating the literals.
/// </summary>
[TestClass]
public class WeatherForecastLimitsTests
{
    [TestMethod]
    public void DisplayModel_FullStripAtFetchCap()
    {
        var daily = Enumerable.Range(0, WeatherForecastLimits.MaxFetchDays)
            .Select(i => new DailyForecastItem($"Day{i}", 20 + i, 10 + i, 1)).ToArray();
        var hourly = Enumerable.Range(0, WeatherForecastLimits.MaxFetchHours)
            .Select(i => new HourlyForecastItem($"{i}:00", 15 + i, 1)).ToArray();
        var input = new WeatherDisplayInput(22.5, new WeatherMetricsInput(false, 0, false, 0, false, 0, false, 0, 0, "°C", "km/h"), daily, hourly);

        var display = WeatherPresentation.Build(input);

        Assert.AreEqual(WeatherForecastLimits.MaxStripDays, display.ForecastRanges.Count,
            "fed the fetch cap (7), the daily strip must still fill all draw columns (5) — draw ≤ fetch");
        Assert.AreEqual(WeatherForecastLimits.MaxStripDays, display.DailyHighLows.Count,
            "the high/low rows follow the same daily draw cap");
        Assert.AreEqual(WeatherForecastLimits.MaxStripHours, display.HourlyTemps.Count,
            "fed the fetch cap (12), the hourly strip must still fill all draw columns (6) — draw ≤ fetch");
    }
}
