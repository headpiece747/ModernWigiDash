
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
    public void NotableDates_GetForMonth_ReturnsHolidaysAndObservances()
    {
        var septDates = CalendarNotableDates.GetForMonth(2026, 9);
        Assert.IsTrue(septDates.Any(d => d.Day == 7 && d.Label == "Labor Day"));
        Assert.IsTrue(septDates.Any(d => d.Day == 22 && d.Label == "Autumn Equinox"));

        var decDates = CalendarNotableDates.GetForMonth(2026, 12);
        Assert.IsTrue(decDates.Any(d => d.Day == 25 && d.Label == "Christmas Day"));
    }
}
