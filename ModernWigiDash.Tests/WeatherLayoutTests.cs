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

    [TestMethod]
    public void ComputeHeader_AtDesignScale_PinsExactGeometry()
    {
        // The design scale (s = sy = 1) pins every header formula exactly:
        // a regression in any clamp or offset changes a value below.
        var bounds = new SKRect(0, 0, WeatherLayout.DesignWidth, WeatherLayout.DesignHeight);
        var header = WeatherLayout.ComputeHeader(bounds, 1f, 1f);

        Assert.AreEqual(14f, header.Pad, "pad = clamp(14·s, 8, 32) at s = 1");
        Assert.AreEqual(44f, header.HeaderHeight, "headerHeight = clamp(44·sy, 24, 90) at sy = 1");
        Assert.AreEqual(54f, header.BadgeRect.Width, "badgeWidth = clamp(54·s, 30, 100) at s = 1");
        Assert.AreEqual(26f, header.BadgeRect.Height, "badgeHeight = clamp(26·sy, 16, 50) at sy = 1");
        Assert.AreEqual(24f, header.TitleFontSize, "titleFontSize = clamp(24·s, 12, 44) at s = 1");
        Assert.AreEqual(338f, header.BadgeRect.Left, "the badge sits right-pad-badgeWidth from the right edge");
        Assert.AreEqual(392f, header.BadgeRect.Right, "the badge ends at right-pad");
        Assert.AreEqual(9f, header.BadgeRect.Top, "the badge is vertically centered in the header band");
        Assert.AreEqual(35f, header.BadgeRect.Bottom);
        // The baseline is 44f * 0.65f: the same float expression the layout
        // computes, so the exact (float-rounded) value is pinned bit-for-bit.
        Assert.AreEqual(44f * 0.65f, header.HeaderTextY, "the title baseline sits at 65% of the header height");
    }

    [TestMethod]
    public void HeroTextStackShrinkScale_And_MetricPillShrinkScale_PinTheirBoundaries()
    {
        // Exactly at the 85% bound the stack fits (no shrink); past it the
        // stack scales down proportionally.
        Assert.AreEqual(1f, WeatherLayout.HeroTextStackShrinkScale(85f, 100f), "the exact 85% bound still fits");
        Assert.AreEqual(1f, WeatherLayout.HeroTextStackShrinkScale(84.99f, 100f));
        Assert.AreEqual(0.85f, WeatherLayout.HeroTextStackShrinkScale(100f, 100f), 0.0001f);
        Assert.AreEqual(0.425f, WeatherLayout.HeroTextStackShrinkScale(200f, 100f), 0.0001f);

        // At/below the content width the pills fit (no shrink); past it they
        // shrink proportionally, floored at 60% of the original width.
        Assert.AreEqual(1f, WeatherLayout.MetricPillShrinkScale(200f, 200f), "the exact width still fits");
        Assert.AreEqual(1f, WeatherLayout.MetricPillShrinkScale(200f, 250f));
        Assert.AreEqual(0.75f, WeatherLayout.MetricPillShrinkScale(200f, 150f), 0.0001f);
        Assert.AreEqual(0.6f, WeatherLayout.MetricPillShrinkScale(200f, 100f), "the 60% floor must win over the ratio");
    }

    [TestMethod]
    public void StripHeights_And_Minimums_PinExactValues()
    {
        // MSTEST0032 folds the const-vs-const comparison as "always true":
        // that is the point of a pin: a regression in the constant changes
        // the actual and fails the assert.
#pragma warning disable MSTEST0032
        Assert.AreEqual(150f, WeatherLayout.StripsMinHeight, "the strips' minimum content height");
#pragma warning restore MSTEST0032
        Assert.AreEqual(80f, WeatherLayout.ForecastStripHeight(1f), "ForecastStripHeight = clamp(80·sy, 45, 160) at sy = 1");
        Assert.AreEqual(28f, WeatherLayout.MetricsStripHeight(1f), "MetricsStripHeight = clamp(28·sy, 16, 50) at sy = 1");
        Assert.AreEqual(45f, WeatherLayout.ForecastStripHeight(0.1f), "the tiny-scale clamp floor");
        Assert.AreEqual(160f, WeatherLayout.ForecastStripHeight(10f), "the huge-scale clamp ceiling");
        Assert.AreEqual(16f, WeatherLayout.MetricsStripHeight(0.1f), "the tiny-scale clamp floor");
        Assert.AreEqual(50f, WeatherLayout.MetricsStripHeight(10f), "the huge-scale clamp ceiling");
    }

    [TestMethod]
    public void DailyRowFontSizes_AtUnitScale_PinExactValues()
    {
        Assert.AreEqual(13f, WeatherLayout.DailyDayFontSize(1f), "DailyDayFontSize = clamp(13·s, 9, 18)");
        Assert.AreEqual(16f, WeatherLayout.DailyIconFontSize(1f), "DailyIconFontSize = clamp(16·s, 10, 22)");
        Assert.AreEqual(11f, WeatherLayout.DailyDescFontSize(1f), "DailyDescFontSize = clamp(11·s, 8, 15)");
        Assert.AreEqual(12f, WeatherLayout.DailyTempFontSize(1f), "DailyTempFontSize = clamp(12·s, 8, 16)");
    }

    [TestMethod]
    public void HourlyFontSizes_AtUnitScale_PinExactValues()
    {
        Assert.AreEqual(11f, WeatherLayout.HourlyTimeFontSize(1f), "HourlyTimeFontSize = clamp(11·s, 8, 15)");
        Assert.AreEqual(12f, WeatherLayout.HourlyTempFontSize(1f), "HourlyTempFontSize = clamp(12·s, 8, 16)");
    }

    [TestMethod]
    public void DetailedHeroRules_PinExactValues()
    {
        Assert.AreEqual(75f, WeatherLayout.DetailedHeroIconSize(100f), "icon = 75% of the hero height");
        Assert.AreEqual(45f, WeatherLayout.DetailedHeroTempSize(100f), "temp = 45% of the hero height");
        Assert.AreEqual(18f, WeatherLayout.DetailedHeroDescSize(100f), "desc = 18% of the hero height");
        Assert.AreEqual(20f, WeatherLayout.DetailedHeroGap(1f), "gap = clamp(20·s, 8, 50) at s = 1");
        Assert.AreEqual(20f, WeatherLayout.DetailedHeroIconSize(10f), "a tiny hero hits the 20px icon floor");
        Assert.AreEqual(220f, WeatherLayout.DetailedHeroIconSize(400f), "a huge hero hits the 220px icon ceiling");
        Assert.AreEqual(14f, WeatherLayout.DetailedHeroTempSize(10f), "a tiny hero hits the 14px temp floor");
        Assert.AreEqual(140f, WeatherLayout.DetailedHeroTempSize(400f), "a huge hero hits the 140px temp ceiling");
        Assert.AreEqual(9f, WeatherLayout.DetailedHeroDescSize(10f), "a tiny hero hits the 9px desc floor");
        Assert.AreEqual(45f, WeatherLayout.DetailedHeroDescSize(400f), "a huge hero hits the 45px desc ceiling");
        Assert.AreEqual(8f, WeatherLayout.DetailedHeroGap(0.1f), "the tiny-scale gap floor");
        Assert.AreEqual(50f, WeatherLayout.DetailedHeroGap(10f), "the huge-scale gap ceiling");
    }

    [TestMethod]
    public void CurrentOnlyCompactAndBadgeSizes_AtUnitScale_PinExactValues()
    {
        Assert.AreEqual(64f, WeatherLayout.CurrentOnlyTempSize(1f), "CurrentOnlyTempSize = clamp(64·s, 28, 84)");
        Assert.AreEqual(24f, WeatherLayout.CurrentOnlyDescSize(1f), "CurrentOnlyDescSize = clamp(24·s, 12, 32)");
        Assert.AreEqual(20f, WeatherLayout.CompactTempFontSize(1f), "CompactTempFontSize = clamp(20·s, 12, 26)");
        Assert.AreEqual(17f, WeatherLayout.BadgeFontSize(1f), "BadgeFontSize = clamp(17·s, 10, 30)");
    }

    [TestMethod]
    public void PillMetrics_AtUnitScale_PinExactValues()
    {
        Assert.AreEqual(13f, WeatherLayout.PillFontSize(1f), "PillFontSize = clamp(13·s, 8, 24)");
        Assert.AreEqual(10f, WeatherLayout.PillPadX(1f), "PillPadX = clamp(10·s, 4, 20)");
        Assert.AreEqual(8f, WeatherLayout.PillGap(1f), "PillGap = clamp(8·s, 3, 16)");
    }

    [TestMethod]
    public void FloorConstants_PinExactValues()
    {
        // MSTEST0032 folds the const-vs-const comparison as "always true":
        // that is the point of a pin: a regression in the constant changes
        // the actual and fails the assert.
#pragma warning disable MSTEST0032
        Assert.AreEqual(35f, WeatherLayout.DetailedHeroMinHeight, "the hero block's minimum height");
        Assert.AreEqual(0.5f, WeatherLayout.HeroBlockNarrowScaleFloor, "the narrow-container auto-scale floor");
        Assert.AreEqual(7f, WeatherLayout.MetricPillFontFloor, "the metric pill font's legibility floor");
#pragma warning restore MSTEST0032
    }
}
