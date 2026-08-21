using System.Globalization;

namespace ModernWigiDash.Tests;

/// <summary>
/// The clock, stopwatch, and ticker display rules — previously inline in the
/// render paths (or private helpers), now pure and assertable.
/// </summary>
[TestClass]
public class ClockStopwatchTickerPresentationTests
{
    // ── ClockPresentation ──

    [TestMethod]
    public void Clock_AmPm_OnlyInTwelveHourMode()
    {
        var noon = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.AreEqual("PM", ClockPresentation.AmPm(noon, "12H"));
        Assert.AreEqual("", ClockPresentation.AmPm(noon, "24H"));
    }

    [TestMethod]
    public void Clock_Date_LongFormatted()
    {
        var day = new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Unspecified);
        Assert.AreEqual("Monday, August 10, 2026", ClockPresentation.Date(day));
    }

    [TestMethod]
    public void Clock_FormatTime_UsesTwelveOrTwentyFourHour()
    {
        var now = new DateTime(2026, 8, 7, 13, 37, 5, DateTimeKind.Unspecified);

        Assert.AreEqual("01:37", ClockPresentation.FormatClockTime(now, "12H"));
        Assert.AreEqual("13:37", ClockPresentation.FormatClockTime(now, "24H"));
    }

    [TestMethod]
    public void Clock_FormatTime_MidnightRollsToTwelve()
    {
        var midnight = new DateTime(2026, 8, 7, 0, 5, 0, DateTimeKind.Unspecified);

        Assert.AreEqual("12:05", ClockPresentation.FormatClockTime(midnight, "12H"));
        Assert.AreEqual("00:05", ClockPresentation.FormatClockTime(midnight, "24H"));
    }

    [TestMethod]
    public void Clock_HandAngles_Noon_PointStraightUp()
    {
        var noon = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Unspecified);

        (float hour, float minute, float second) = ClockPresentation.HandAngles(noon);

        Assert.AreEqual(0f, hour, 1e-4f, "12:00:00 puts every hand straight up (0 rad)");
        Assert.AreEqual(0f, minute, 1e-4f);
        Assert.AreEqual(0f, second, 1e-4f);
    }

    [TestMethod]
    public void Clock_HandAngles_ThreeOClock_HourAtNinetyDegrees()
    {
        var three = new DateTime(2026, 8, 10, 15, 0, 0, DateTimeKind.Unspecified);

        (float hour, float minute, float second) = ClockPresentation.HandAngles(three);

        Assert.AreEqual((float)(Math.PI / 2), hour, 1e-4f, "3 o'clock reads 90° for the hour hand");
        Assert.AreEqual(0f, minute, 1e-4f);
        Assert.AreEqual(0f, second, 1e-4f);
    }

    [TestMethod]
    public void Clock_HandAngles_Sweep_MinutesCarryIntoHands()
    {
        var sweep = new DateTime(2026, 8, 10, 6, 30, 30, DateTimeKind.Unspecified);

        (float hour, float minute, float second) = ClockPresentation.HandAngles(sweep);

        Assert.AreEqual(195f * (float)(Math.PI / 180f), hour, 1e-4f, "6:30 puts the hour hand halfway to 7");
        Assert.AreEqual(183f * (float)(Math.PI / 180f), minute, 1e-4f, "the 30s carry moves the minute hand 3° past 6");
        Assert.AreEqual(180f * (float)(Math.PI / 180f), second, 1e-4f);
    }

    // ── StopwatchPresentation ──

    [TestMethod]
    public void Stopwatch_FormatElapsed_CentisecondsWithLeadingZeros()
    {
        Assert.AreEqual("00:00.00", StopwatchPresentation.FormatElapsed(TimeSpan.Zero));
        Assert.AreEqual("01:05.23", StopwatchPresentation.FormatElapsed(TimeSpan.FromMilliseconds(65_230)));
        Assert.AreEqual("00:07.00", StopwatchPresentation.FormatElapsed(TimeSpan.FromSeconds(7)));
    }

    [TestMethod]
    public void Stopwatch_FormatElapsed_MinutesRollWithoutHoursField()
    {
        Assert.AreEqual("01:00.00", StopwatchPresentation.FormatElapsed(TimeSpan.FromMinutes(61)),
            "the long-standing format has no hours field — 61 minutes reads 01:00");
    }

    [TestMethod]
    public void Stopwatch_StatusText_And_StatusColor_MatchState()
    {
        Assert.AreEqual("TAP TO PAUSE", StopwatchPresentation.StatusText(true));
        Assert.AreEqual("TAP TO START", StopwatchPresentation.StatusText(false));
        Assert.AreEqual(new SkiaSharp.SKColor(239, 68, 68), StopwatchPresentation.StatusColor(true));
        Assert.AreEqual(new SkiaSharp.SKColor(34, 197, 94), StopwatchPresentation.StatusColor(false));
    }

    // ── TickerPresentation ──

    [TestMethod]
    public void Ticker_DecimalsFor_ExplicitChoiceWins()
    {
        Assert.AreEqual(2, TickerPresentation.DecimalsFor("2", 0.00001m));
        Assert.AreEqual(8, TickerPresentation.DecimalsFor("8", 12345m));
    }

    [TestMethod]
    public void Ticker_DecimalsFor_PriceTierRules()
    {
        Assert.AreEqual(2, TickerPresentation.DecimalsFor("", 100m));
        Assert.AreEqual(2, TickerPresentation.DecimalsFor("", 999.99m));
        Assert.AreEqual(4, TickerPresentation.DecimalsFor("", 1m));
        Assert.AreEqual(4, TickerPresentation.DecimalsFor("", 99.99m));
        Assert.AreEqual(6, TickerPresentation.DecimalsFor("", 0.01m));
        Assert.AreEqual(6, TickerPresentation.DecimalsFor("", 0.99m));
        Assert.AreEqual(8, TickerPresentation.DecimalsFor("", 0.0099m), "sub-cent prices keep their precision");
    }

    [TestMethod]
    public void Ticker_FormatPrice_TieredDecimalsWithSymbol()
    {
        Assert.AreEqual("$1,234.56", TickerPresentation.FormatPrice(1234.56m, ""));
        Assert.AreEqual("$0.00001234", TickerPresentation.FormatPrice(0.00001234m, ""));
        Assert.AreEqual("€99.9900", TickerPresentation.FormatPrice(99.99m, "", "€"), "99.99 sits in the 4-decimal tier");
    }

    [TestMethod]
    public void Ticker_DecimalsFor_ZeroPrice_TwoDecimals()
    {
        // A zero price is never a tiny crypto price — it formats at the
        // 2-decimal tier instead of "$0.00000000".
        Assert.AreEqual(2, TickerPresentation.DecimalsFor("", 0m));
        Assert.AreEqual(2, TickerPresentation.DecimalsFor("auto", 0m));
    }

    [TestMethod]
    public void Ticker_FormatPrice_IsInvariantCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");

            // A comma-decimal locale must not render "1.234,56" on the display.
            Assert.AreEqual("$1,234.56", TickerPresentation.FormatPrice(1234.56m, "2"));
            Assert.AreEqual("$0.00000000", TickerPresentation.FormatPrice(0m, "8"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [TestMethod]
    public void Ticker_DisplayLabel_FallbackOrder()
    {
        Assert.AreEqual("My Label", TickerPresentation.DisplayLabel("My Label", "EUR / USD", "EURUSD"));
        Assert.AreEqual("EUR / USD", TickerPresentation.DisplayLabel("", "EUR / USD", "EURUSD"));
        Assert.AreEqual("EURUSD", TickerPresentation.DisplayLabel("", null, "EURUSD"));
    }
}
