
namespace ModernWigiDash.Tests;

[TestClass]
public class CalendarAdaptiveLayoutTests
{
    [TestMethod]
    public void ResolveViewMode_Full5x4_ResolvesEditorial()
    {
        var mode = CalendarLayout.ResolveViewMode(1016f, 592f);
        Assert.AreEqual(CalendarViewMode.FullEditorial5x4, mode);
    }

    [TestMethod]
    public void ResolveViewMode_Split4x2_ResolvesSplitBanner()
    {
        var mode = CalendarLayout.ResolveViewMode(812f, 296f);
        Assert.AreEqual(CalendarViewMode.SplitBanner4x2, mode);
    }

    [TestMethod]
    public void ResolveViewMode_Compact2x3_ResolvesCompactPoster()
    {
        var mode = CalendarLayout.ResolveViewMode(406f, 444f);
        Assert.AreEqual(CalendarViewMode.CompactPoster2x3, mode);
    }

    [TestMethod]
    public void ResolveViewMode_LayoutOverride_Respected()
    {
        var modePoster = CalendarLayout.ResolveViewMode(1016f, 592f, "Poster Month");
        Assert.AreEqual(CalendarViewMode.CompactPoster2x3, modePoster);

        var modeAgenda = CalendarLayout.ResolveViewMode(406f, 444f, "Agenda");
        Assert.AreEqual(CalendarViewMode.SplitBanner4x2, modeAgenda);
    }

    [TestMethod]
    public void Compute_FullEditorial5x4_NonOverlappingPanels()
    {
        var bounds = new SKRect(0, 0, 1016, 592);
        var geo = CalendarLayout.Compute(bounds, 2.46f, rowCount: 3, hasAllDay: true);

        Assert.AreEqual(CalendarViewMode.FullEditorial5x4, geo.Mode);
        Assert.IsFalse(geo.LeftStripRect.IsEmpty);
        Assert.IsFalse(geo.MonthCardRect.IsEmpty);
        Assert.IsFalse(geo.AgendaRect.IsEmpty);

        // Panels must be arranged left-to-right without overlapping
        Assert.IsTrue(geo.LeftStripRect.Right < geo.MonthCardRect.Left);
        Assert.IsTrue(geo.MonthCardRect.Right < geo.AgendaRect.Left);

        // Header, Grid, and Footer inside MonthCardRect
        Assert.IsTrue(geo.MonthCardRect.Contains(geo.HeaderRect));
        Assert.IsTrue(geo.MonthCardRect.Contains(geo.MonthGridRect));
        Assert.IsTrue(geo.MonthCardRect.Contains(geo.NotableDatesRect));

        // Chevron targets within HeaderRect
        Assert.IsTrue(geo.HeaderRect.Contains(geo.PrevChevronRect));
        Assert.IsTrue(geo.HeaderRect.Contains(geo.NextChevronRect));

        // Hero and Rows inside AgendaRect
        Assert.IsTrue(geo.AgendaRect.Contains(geo.AllDayRect));
        Assert.AreEqual(3, geo.RowRects.Count);
        foreach (var r in geo.RowRects)
        {
            Assert.IsTrue(geo.AgendaRect.Contains(r));
        }

        // Chevron hit tests
        Assert.IsTrue(CalendarLayout.IsPrevChevronHit(geo, geo.PrevChevronRect.MidX, geo.PrevChevronRect.MidY));
        Assert.IsTrue(CalendarLayout.IsNextChevronHit(geo, geo.NextChevronRect.MidX, geo.NextChevronRect.MidY));
        Assert.IsFalse(CalendarLayout.IsPrevChevronHit(geo, geo.NextChevronRect.MidX, geo.NextChevronRect.MidY));
    }

    [TestMethod]
    public void Compute_SplitBanner4x2_TwoColumns()
    {
        var bounds = new SKRect(0, 0, 812, 296);
        var geo = CalendarLayout.Compute(bounds, 1.8f, rowCount: 2, hasAllDay: false);

        Assert.AreEqual(CalendarViewMode.SplitBanner4x2, geo.Mode);
        Assert.IsTrue(geo.LeftStripRect.IsEmpty);
        Assert.IsFalse(geo.MonthCardRect.IsEmpty);
        Assert.IsFalse(geo.AgendaRect.IsEmpty);

        Assert.IsTrue(geo.MonthCardRect.Right < geo.AgendaRect.Left);
        Assert.IsTrue(geo.MonthCardRect.Contains(geo.HeaderRect));
        Assert.IsTrue(geo.MonthCardRect.Contains(geo.MonthGridRect));

        Assert.AreEqual(2, geo.RowRects.Count);
        Assert.IsTrue(geo.AgendaRect.Contains(geo.RowRects[0]));
    }

    [TestMethod]
    public void Compute_CompactPoster2x3_FullHeightCard()
    {
        var bounds = new SKRect(0, 0, 406, 444);
        var geo = CalendarLayout.Compute(bounds, 1.4f, rowCount: 1, hasAllDay: false);

        Assert.AreEqual(CalendarViewMode.CompactPoster2x3, geo.Mode);
        Assert.IsTrue(geo.LeftStripRect.IsEmpty);
        Assert.IsTrue(geo.AgendaRect.IsEmpty);
        Assert.IsFalse(geo.MonthCardRect.IsEmpty);

        Assert.IsTrue(geo.MonthCardRect.Contains(geo.HeaderRect));
        Assert.IsTrue(geo.MonthCardRect.Contains(geo.MonthGridRect));
        Assert.AreEqual(1, geo.RowRects.Count);
        Assert.IsTrue(geo.MonthCardRect.Contains(geo.RowRects[0]));
    }

    [TestMethod]
    public void ResolveViewMode_Minimal1x1_ResolvesMinimalDateCard()
    {
        var mode = CalendarLayout.ResolveViewMode(203f, 148f);
        Assert.AreEqual(CalendarViewMode.MinimalDateCard1x1, mode);

        var modeOverride = CalendarLayout.ResolveViewMode(500f, 300f, "Minimal Card");
        Assert.AreEqual(CalendarViewMode.MinimalDateCard1x1, modeOverride);
    }

    [TestMethod]
    public void Compute_MinimalDateCard1x1_CardRectDefined()
    {
        var bounds = new SKRect(0, 0, 203f, 148f);
        var geo = CalendarLayout.Compute(bounds, 1.0f, rowCount: 0, hasAllDay: false);

        Assert.AreEqual(CalendarViewMode.MinimalDateCard1x1, geo.Mode);
        Assert.IsFalse(geo.MonthCardRect.IsEmpty);
        Assert.IsTrue(bounds.Contains(geo.MonthCardRect));
    }

    [TestMethod]
    public void Compute_FullEditorial5x4_AgendaScrollAreaRect_Defined()
    {
        var bounds = new SKRect(0, 0, 1016, 592);
        var geo = CalendarLayout.Compute(bounds, 2.46f, rowCount: 6, hasAllDay: true);

        Assert.IsFalse(geo.AgendaScrollAreaRect.IsEmpty);
        Assert.IsTrue(geo.AgendaRect.Contains(geo.AgendaScrollAreaRect));
        Assert.AreEqual(6, geo.RowRects.Count);
    }

    [TestMethod]
    public void GetAction_ScrolledAgendaRows_HitTestsScrolledOffset()
    {
        var bounds = new SKRect(0, 0, 1016, 592);
        var geo = CalendarLayout.Compute(bounds, 2.46f, rowCount: 8, hasAllDay: true);

        Assert.IsTrue(geo.RowRects.Count >= 4);
        SKRect row3 = geo.RowRects[3]; // 4th row, normally scrolled down
        float scrollOffset = row3.Top - geo.AgendaScrollAreaRect.Top;

        // If we test with scrollY = scrollOffset, a point inside AgendaScrollAreaRect at row3.Top - scrollOffset should hit row index 3
        int hitIndex = CalendarLayout.GetAction(geo, row3.MidX, geo.AgendaScrollAreaRect.Top + 5f, scrollOffset, out _);
        Assert.AreEqual(3, hitIndex);
    }
}
