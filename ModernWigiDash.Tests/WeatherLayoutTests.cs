using ModernWigiDash.Widgets;
using SkiaSharp;

namespace ModernWigiDash.Tests;

/// <summary>
/// The weather layout policy: every font size / offset constant the renderers
/// and the widget share — one owner, so draw and measurement cannot drift.
/// </summary>
[TestClass]
public class WeatherLayoutTests
{
    [TestMethod]
    public void FontSizes_AreClampedAndScaled()
    {
        // Scaled from a unit scale; the clamps bound the extremes.
        Assert.AreEqual(8f, WeatherLayout.ForecastDayFontSize(0.1f), "a tiny scale clamps to the minimum");
        Assert.AreEqual(24f, WeatherLayout.ForecastDayFontSize(10f), "a huge scale clamps to the maximum");
        float mid = WeatherLayout.ForecastDayFontSize(1f);
        Assert.IsTrue(mid is > 8f and < 24f, "a unit scale lands inside the clamp range");
    }

    [TestMethod]
    public void FontSizes_MonotonicInScale()
    {
        Assert.IsTrue(WeatherLayout.ForecastDayFontSize(0.5f) < WeatherLayout.ForecastDayFontSize(1f),
            "a larger scale must never produce a smaller font");
        Assert.IsTrue(WeatherLayout.HourlyTempFontSize(0.5f) < WeatherLayout.HourlyTempFontSize(1f));
        Assert.IsTrue(WeatherLayout.CompactIconFontSize(0.5f) < WeatherLayout.CompactIconFontSize(1f));
    }

    [TestMethod]
    public void TitleMaxWidth_ReservesBadgeAndPadding()
    {
        // 500 wide, 8 pad, 40 badge → 500 − 2·8 − 40 = 444 exactly.
        Assert.AreEqual(444f, WeatherLayout.TitleMaxWidth(500f, 8f, 40f),
            "the title must yield the badge and both paddings, computed exactly");
        // 100 − 2·8 − 90 = −6: the absolute-minimum 30px floor must win.
        Assert.AreEqual(30f, WeatherLayout.TitleMaxWidth(100f, 8f, 90f),
            "the title keeps its absolute minimum width when the content would collapse below it");
    }

    [TestMethod]
    public void CurrentOnlyIconSize_ClampsToHeroExtremes()
    {
        // The CurrentOnly hero pins its icon to 40..120 (the 20..220 range
        // belongs to the Detailed hero) — pin the REAL bounds so a regression
        // in the clamp cannot slip through a stale assertion.
        Assert.AreEqual(40f, WeatherLayout.CurrentOnlyIconSize(0.1f), "a tiny hero still gets a legible icon");
        Assert.AreEqual(120f, WeatherLayout.CurrentOnlyIconSize(10f), "a huge hero caps the icon size");
    }

    [TestMethod]
    public void GetHeaderAction_BadgeAndCycleZoneGeometry()
    {
        // The touch zones come from the SAME ComputeHeader the render path
        // draws: the badge rect is the unit-toggle target, the left header
        // band is the layout-cycle target, everything else reads None.
        var bounds = new SKRect(0, 0, WeatherLayout.DesignWidth, WeatherLayout.DesignHeight);
        var (_, sy, s) = WeatherLayout.Scale(bounds);
        var header = WeatherLayout.ComputeHeader(bounds, s, sy);

        Assert.AreEqual(WeatherHeaderAction.ToggleUnit,
            WeatherLayout.GetHeaderAction(bounds, new SKPoint(header.BadgeRect.MidX, header.BadgeRect.MidY), s, sy),
            "a tap inside the badge rect must toggle the unit");

        Assert.AreEqual(WeatherHeaderAction.CycleLayout,
            WeatherLayout.GetHeaderAction(bounds, new SKPoint(20f, 15f), s, sy),
            "a tap in the left header band must cycle the layout");

        Assert.AreEqual(WeatherHeaderAction.None,
            WeatherLayout.GetHeaderAction(bounds, new SKPoint(300f, 15f), s, sy),
            "a tap right of the cycle zone inside the header band reads None");

        Assert.AreEqual(WeatherHeaderAction.None,
            WeatherLayout.GetHeaderAction(bounds, new SKPoint(20f, header.HeaderHeight + 20f), s, sy),
            "a tap below the header band reads None");
    }

    [TestMethod]
    public void NextMode_GarbageResetsToTheDefault()
    {
        // A hand-edited profile value ("garbage") parses to the default, and
        // the cycle must LAND on that default — not advance past it.
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.NextMode("garbage"));
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.NextMode(""));
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.NextMode(null));

        Assert.AreEqual(WeatherLayoutMode.DailyForecast, WeatherLayout.NextMode("Detailed"));
        Assert.AreEqual(WeatherLayoutMode.HourlyForecast, WeatherLayout.NextMode("Daily Forecast"));
        Assert.AreEqual(WeatherLayoutMode.CurrentOnly, WeatherLayout.NextMode("Hourly Forecast"));
        Assert.AreEqual(WeatherLayoutMode.Compact, WeatherLayout.NextMode("Current Only"));
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.NextMode("Compact"), "the wrap resets to the default");
    }
}
