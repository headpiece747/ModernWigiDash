
namespace ModernWigiDash.Tests;

[TestClass]
public class CalendarSeasonalPaletteTests
{
    [TestMethod]
    public void GetForMonth_AllTwelveMonths_ReturnsDistinctPalettes()
    {
        string[] expectedCodes = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
        var backgrounds = new HashSet<SKColor>();

        for (int m = 1; m <= 12; m++)
        {
            var p = CalendarSeasonalPalettes.GetForMonth(m);
            Assert.AreEqual(expectedCodes[m - 1], p.MonthCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(p.FullMonthName));
            backgrounds.Add(p.Background);
        }

        // All 12 months should have distinct signature background colors
        Assert.AreEqual(12, backgrounds.Count);
    }

    [TestMethod]
    public void Resolve_SeasonalMode_ReturnsMonthPalette()
    {
        var sepDate = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Unspecified);
        var p = CalendarSeasonalPalettes.Resolve(sepDate, "Seasonal", "#000000", "#FFFFFF");

        Assert.AreEqual("SEP", p.MonthCode);
        Assert.AreEqual("September", p.FullMonthName);
        Assert.AreEqual(new SKColor(255, 77, 45), p.Background); // Tangerine
    }

    [TestMethod]
    public void Resolve_CustomMode_UsesCustomColors()
    {
        var date = new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Unspecified);
        var p = CalendarSeasonalPalettes.Resolve(date, "Custom", "#10B981", "#E2E8F0");

        Assert.AreEqual(new SKColor(16, 185, 129), p.Accent);
        Assert.AreEqual(new SKColor(226, 232, 240), p.Text);
        Assert.AreEqual("SEP", p.MonthCode);
    }

    [TestMethod]
    public void ResolveCustom_UnparseableHexes_FallBackToNamedDefaults()
    {
        var date = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Unspecified);
        var p = CalendarSeasonalPalettes.ResolveCustom(date, "not-a-color", "");

        // Unparseable accent falls back to the named blue; unparseable text to white.
        Assert.AreEqual(new SKColor(79, 140, 255), p.Accent);
        Assert.AreEqual(SKColors.White, p.Text);
        // The fixed deep slate background is not user-supplied.
        Assert.AreEqual(new SKColor(18, 20, 29), p.Background);
        Assert.AreEqual("MAR", p.MonthCode);
        Assert.AreEqual("March", p.FullMonthName);
    }

    [TestMethod]
    public void ResolveCustom_ClampsOutOfRangeMonth()
    {
        // Month 0 clamps to January (index 0) rather than throwing.
        var p = CalendarSeasonalPalettes.ResolveCustom(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), "#FF0000", "#00FF00");
        Assert.AreEqual("JAN", p.MonthCode);
    }

    [TestMethod]
    public void NotableDates_GetForMonth_ReturnsHolidaysAndObservances()
    {
        var septDates = CalendarNotableDates.GetForMonth(2026, 9);
        Assert.IsTrue(septDates.Any(d => d.Day == 7 && d.Label == "Labor Day"));
        Assert.IsTrue(septDates.Any(d => d.Day == 22 && d.Label == "Autumn Equinox"));

        var decDates = CalendarNotableDates.GetForMonth(2026, 12);
        Assert.IsTrue(decDates.Any(d => d.Day == 25 && d.Label == "Christmas Day"));
    }

    [TestMethod]
    public void NotableDates_FloatingObservances_LandOnTheRightWeekday()
    {
        // MLK Jr. Day: 3rd Monday in Jan 2026 (Jan 1 is a Thursday -> 3rd Mon = 19).
        var jan = CalendarNotableDates.GetForMonth(2026, 1);
        Assert.IsTrue(jan.Any(d => d.Day == 19 && d.Label == "MLK Jr. Day"));

        // Presidents' Day: 3rd Monday in Feb 2026 (Feb 1 is a Sunday -> 3rd Mon = 16).
        var feb = CalendarNotableDates.GetForMonth(2026, 2);
        Assert.IsTrue(feb.Any(d => d.Day == 16 && d.Label == "Presidents' Day"));

        // Memorial Day: last Monday in May 2026 (May 31 is a Sunday -> last Mon = 25).
        var may = CalendarNotableDates.GetForMonth(2026, 5);
        Assert.IsTrue(may.Any(d => d.Day == 25 && d.Label == "Memorial Day"));

        // Thanksgiving: 4th Thursday in Nov 2026 (Nov 1 is a Sunday -> 4th Thu = 26).
        var nov = CalendarNotableDates.GetForMonth(2026, 11);
        Assert.IsTrue(nov.Any(d => d.Day == 26 && d.Label == "Thanksgiving"));
    }
}
