using SkiaSharp;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Weather widget's layout rules: the scale factors, the header geometry
/// (title, unit badge, content padding), the header touch zones, and the
/// layout-mode cycle. Previously split between the render path and OnTouch's
/// independent constants — the drawn geometry and the tap targets now share
/// this module.
/// </summary>
[TestClass]
public class WeatherLayoutTests
{
    [TestMethod]
    public void Scale_DesignSize_IsOneToOne()
    {
        var scale = WeatherLayout.Scale(new SKRect(0, 0, 406, 296));

        Assert.AreEqual(1f, scale.Sx, 0.001f);
        Assert.AreEqual(1f, scale.Sy, 0.001f);
        Assert.AreEqual(1f, scale.S, 0.001f);
    }

    [TestMethod]
    public void Scale_NonUniform_UniformIsTheMin()
    {
        var scale = WeatherLayout.Scale(new SKRect(0, 0, 812, 296));

        Assert.AreEqual(2f, scale.Sx, 0.001f);
        Assert.AreEqual(1f, scale.Sy, 0.001f);
        Assert.AreEqual(1f, scale.S, 0.001f);
    }

    [TestMethod]
    public void ComputeHeader_DesignSize_MatchesDrawnGeometry()
    {
        var header = WeatherLayout.ComputeHeader(new SKRect(0, 0, 406, 296), 1f, 1f);

        Assert.AreEqual(44f, header.HeaderHeight, 0.001f);
        Assert.AreEqual(28.6f, header.HeaderTextY, 0.001f);
        Assert.AreEqual(24f, header.TitleFontSize, 0.001f);
        Assert.AreEqual(14f, header.Pad, 0.001f);
        Assert.AreEqual(new SKRect(338, 9, 392, 35), header.BadgeRect);
    }

    [TestMethod]
    public void ComputeHeader_SmallSize_ClampsToMinimums()
    {
        var header = WeatherLayout.ComputeHeader(new SKRect(0, 0, 200, 160), 200f / 406f, 160f / 296f);

        Assert.AreEqual(24f, header.HeaderHeight, 0.001f);
        Assert.AreEqual(15.6f, header.HeaderTextY, 0.001f);
        Assert.AreEqual(12f, header.TitleFontSize, 0.001f);
        Assert.AreEqual(8f, header.Pad, 0.001f);
        Assert.AreEqual(new SKRect(162, 4, 192, 20), header.BadgeRect);
    }

    [TestMethod]
    public void ComputeHeader_LargeSize_ClampsToMaximums()
    {
        var header = WeatherLayout.ComputeHeader(new SKRect(0, 0, 812, 592), 2f, 2f);

        Assert.AreEqual(88f, header.HeaderHeight, 0.001f);
        Assert.AreEqual(57.2f, header.HeaderTextY, 0.001f);
        Assert.AreEqual(44f, header.TitleFontSize, 0.001f);
        Assert.AreEqual(28f, header.Pad, 0.001f);
        Assert.AreEqual(new SKRect(684, 19, 784, 69), header.BadgeRect);
    }

    [TestMethod]
    public void GetHeaderAction_BadgeCenter_TogglesUnit()
    {
        // Full-screen fallback bounds — the geometry the widget uses before
        // its first render (DefaultSize = 1016 x 592).
        var bounds = new SKRect(0, 0, 1016, 592);
        var scale = WeatherLayout.Scale(bounds);

        var action = WeatherLayout.GetHeaderAction(bounds, new SKPoint(940, 40), scale.S, scale.Sy);

        Assert.AreEqual(WeatherHeaderAction.ToggleUnit, action);
    }

    [TestMethod]
    public void GetHeaderAction_LeftHeaderZone_CyclesLayout()
    {
        var bounds = new SKRect(0, 0, 1016, 592);
        var scale = WeatherLayout.Scale(bounds);

        var action = WeatherLayout.GetHeaderAction(bounds, new SKPoint(20, 15), scale.S, scale.Sy);

        Assert.AreEqual(WeatherHeaderAction.CycleLayout, action);
    }

    [TestMethod]
    public void GetHeaderAction_BelowHeader_None()
    {
        var bounds = new SKRect(0, 0, 406, 296);

        var action = WeatherLayout.GetHeaderAction(bounds, new SKPoint(200, 200), 1f, 1f);

        Assert.AreEqual(WeatherHeaderAction.None, action);
    }

    [TestMethod]
    public void GetHeaderAction_RightOfBadge_None()
    {
        // The old 64px tap strip beyond the badge is not a target anymore — the
        // badge rect is the honest target (the badge is what the user sees).
        var bounds = new SKRect(0, 0, 406, 296);

        var action = WeatherLayout.GetHeaderAction(bounds, new SKPoint(400, 20), 1f, 1f);

        Assert.AreEqual(WeatherHeaderAction.None, action);
    }

    [TestMethod]
    public void GetHeaderAction_BadgeXButBelowBadge_None()
    {
        var bounds = new SKRect(0, 0, 406, 296);

        var action = WeatherLayout.GetHeaderAction(bounds, new SKPoint(360, 40), 1f, 1f);

        Assert.AreEqual(WeatherHeaderAction.None, action);
    }

    [TestMethod]
    public void NextMode_CycleOrder_MatchesWidgetCycle()
    {
        Assert.AreEqual(WeatherLayoutMode.DailyForecast, WeatherLayout.NextMode(WeatherLayoutMode.Detailed));
        Assert.AreEqual(WeatherLayoutMode.HourlyForecast, WeatherLayout.NextMode(WeatherLayoutMode.DailyForecast));
        Assert.AreEqual(WeatherLayoutMode.CurrentOnly, WeatherLayout.NextMode(WeatherLayoutMode.HourlyForecast));
        Assert.AreEqual(WeatherLayoutMode.Compact, WeatherLayout.NextMode(WeatherLayoutMode.CurrentOnly));
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.NextMode(WeatherLayoutMode.Compact));
    }

    [TestMethod]
    public void DisplayName_EachMode_MatchesInspectorChoiceStrings()
    {
        Assert.AreEqual(WeatherLayout.DefaultLayoutMode, WeatherLayout.DisplayName(WeatherLayoutMode.Detailed));
        Assert.AreEqual("Daily Forecast", WeatherLayout.DisplayName(WeatherLayoutMode.DailyForecast));
        Assert.AreEqual("Hourly Forecast", WeatherLayout.DisplayName(WeatherLayoutMode.HourlyForecast));
        Assert.AreEqual("Current Only", WeatherLayout.DisplayName(WeatherLayoutMode.CurrentOnly));
        Assert.AreEqual("Compact", WeatherLayout.DisplayName(WeatherLayoutMode.Compact));
    }

    [TestMethod]
    public void ParseMode_KnownModes_MapExactly()
    {
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.ParseMode("Detailed"));
        Assert.AreEqual(WeatherLayoutMode.DailyForecast, WeatherLayout.ParseMode("Daily Forecast"));
        Assert.AreEqual(WeatherLayoutMode.HourlyForecast, WeatherLayout.ParseMode("Hourly Forecast"));
        Assert.AreEqual(WeatherLayoutMode.CurrentOnly, WeatherLayout.ParseMode("Current Only"));
        Assert.AreEqual(WeatherLayoutMode.Compact, WeatherLayout.ParseMode("Compact"));
    }

    [TestMethod]
    public void ParseMode_UnknownMode_DefaultsToDetailed()
    {
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.ParseMode("Bogus"));
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.ParseMode(null));
    }

    [TestMethod]
    public void NextMode_UnknownMode_LandsOnDefault()
    {
        // A hand-edited profile with an unknown LayoutMode string must reset
        // to the default on tap — not advance past it (the OLD bug: garbage
        // parsed to Detailed, then the cycle stepped it to Daily Forecast).
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.NextMode("Bogus"));
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.NextMode(null));
        Assert.AreEqual(WeatherLayoutMode.Detailed, WeatherLayout.NextMode(""));
        Assert.AreEqual(WeatherLayoutMode.DailyForecast, WeatherLayout.NextMode("Detailed"), "a known mode still advances");
    }

    [TestMethod]
    public void HeroTextStackShrinkScale_Overflow_ScalesTo85PercentOfHeroHeight()
    {
        Assert.AreEqual(0.425f, WeatherLayout.HeroTextStackShrinkScale(200f, 100f), 0.001f);
    }

    [TestMethod]
    public void HeroTextStackShrinkScale_Fits_NoShrink()
    {
        Assert.AreEqual(1f, WeatherLayout.HeroTextStackShrinkScale(80f, 100f), 0.001f);
    }

    [TestMethod]
    public void MetricPillShrinkScale_Overflow_ProportionalWithFloor()
    {
        Assert.AreEqual(0.6667f, WeatherLayout.MetricPillShrinkScale(300f, 200f), 0.001f);
        Assert.AreEqual(0.6f, WeatherLayout.MetricPillShrinkScale(500f, 100f), 0.001f);
    }

    [TestMethod]
    public void MetricPillShrinkScale_Fits_NoShrink()
    {
        Assert.AreEqual(1f, WeatherLayout.MetricPillShrinkScale(150f, 200f), 0.001f);
    }

    [TestMethod]
    public void PillFontSize_ClampsToTheDrawRange()
    {
        Assert.AreEqual(8f, WeatherLayout.PillFontSize(0.1f), "tiny scales clamp to the minimum");
        Assert.AreEqual(24f, WeatherLayout.PillFontSize(3f), "huge scales clamp to the maximum");
        Assert.AreEqual(13f, WeatherLayout.PillFontSize(1f), 0.001f, "the design scale uses the base size");
    }

    [TestMethod]
    public void PillPadX_ClampsToTheDrawRange()
    {
        Assert.AreEqual(4f, WeatherLayout.PillPadX(0.1f));
        Assert.AreEqual(20f, WeatherLayout.PillPadX(3f));
        Assert.AreEqual(10f, WeatherLayout.PillPadX(1f), 0.001f);
    }

    [TestMethod]
    public void PillGap_ClampsToTheDrawRange()
    {
        Assert.AreEqual(3f, WeatherLayout.PillGap(0.1f));
        Assert.AreEqual(16f, WeatherLayout.PillGap(3f));
        Assert.AreEqual(8f, WeatherLayout.PillGap(1f), 0.001f);
    }
}
