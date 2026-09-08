
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

    [TestMethod]
    public void Compute_FullEditorial5x4_MonthGridGeometry_ThirtyFiveCells()
    {
        var bounds = new SKRect(0, 0, 1016, 592);
        var geo = CalendarLayout.Compute(bounds, 2.46f, rowCount: 3, hasAllDay: true);

        // The month-grid geometry is the one source of truth for the intra-panel
        // cell math: 35 cells (7 columns x 5 rows), each with a center and an
        // event-dot center, plus the weekday-header height the renderer and the
        // touch path both read instead of re-deriving.
        Assert.AreEqual(35, geo.MonthGrid.Cells.Count);
        Assert.IsTrue(geo.MonthGrid.WeekdayHeaderHeight > 0f, "a weekday header is drawn above the cells");

        foreach (MonthGridCell cell in geo.MonthGrid.Cells)
        {
            // Every cell sits inside the month grid rect.
            Assert.IsTrue(geo.MonthGridRect.Contains(cell.Rect));
            // The day-number center is inside its own cell.
            Assert.IsTrue(cell.Rect.Contains(cell.Center.X, cell.Center.Y));
            // The event dot sits below the day number (the marker's offset).
            Assert.IsTrue(cell.DotCenter.Y > cell.Center.Y);
        }
    }

    [TestMethod]
    public void MonthGridGeometry_CellCenters_TileTheGridInSevenColumns()
    {
        var bounds = new SKRect(0, 0, 1016, 592);
        var geo = CalendarLayout.Compute(bounds, 2.46f, rowCount: 3, hasAllDay: true);

        IReadOnlyList<MonthGridCell> cells = geo.MonthGrid.Cells;
        // Cells in the same column share an X; consecutive columns step right by
        // the cell width (gridWidth / 7). This pins the tiling the renderer used
        // to compute on its own.
        float colStep = geo.MonthGridRect.Width / 7f;
        for (int col = 0; col < 7; col++)
        {
            float expectedX = geo.MonthGridRect.Left + col * colStep + colStep / 2f;
            for (int row = 0; row < 5; row++)
            {
                MonthGridCell cell = cells[row * 7 + col];
                Assert.AreEqual(expectedX, cell.Center.X, 0.5f, $"cell ({row},{col}) center X tiles the grid");
            }
        }
    }

    [TestMethod]
    public void MonthGridGeometry_EmptyWhenNoGridRect()
    {
        // A mode without a month grid emits an empty geometry (no cells).
        var bounds = new SKRect(0, 0, 203f, 148f);
        var geo = CalendarLayout.Compute(bounds, 1.0f, rowCount: 0, hasAllDay: false);

        Assert.AreEqual(0, geo.MonthGrid.Cells.Count);
    }

    [TestMethod]
    public void BuildAgendaScrollGeometry_NoExtent_NotVisible()
    {
        // Nothing to scroll: the thumb is hidden (the renderer draws nothing).
        var area = new SKRect(10f, 100f, 300f, 400f);
        var g = CalendarLayout.BuildAgendaScrollGeometry(area, maxScrollY: 0f, scrollY: 0f, scale: 1f);
        Assert.IsFalse(g.Visible);
    }

    [TestMethod]
    public void BuildAgendaScrollGeometry_EmptyArea_NotVisible()
    {
        // No scroll area at all: hidden even with a positive extent.
        var g = CalendarLayout.BuildAgendaScrollGeometry(SKRect.Empty, maxScrollY: 50f, scrollY: 10f, scale: 1f);
        Assert.IsFalse(g.Visible);
    }

    [TestMethod]
    public void BuildAgendaScrollGeometry_TopOffset_ThumbAtTrackTop()
    {
        // At offset zero the thumb sits at the track's top inset; its width is the
        // fixed 3*scale and it hugs the right edge of the scroll area.
        var area = new SKRect(10f, 100f, 300f, 400f); // height 300
        var g = CalendarLayout.BuildAgendaScrollGeometry(area, maxScrollY: 300f, scrollY: 0f, scale: 1f);
        Assert.IsTrue(g.Visible);
        Assert.AreEqual(100f + 4f, g.ThumbY, 0.01f, "thumb starts at the top inset");
        Assert.AreEqual(300f - 5f, g.ThumbX, 0.01f, "thumb hugs the right edge");
        Assert.AreEqual(3f, g.ThumbWidth, 0.01f, "fixed thumb width");
    }

    [TestMethod]
    public void BuildAgendaScrollGeometry_FullOffset_ThumbAtTrackBottom()
    {
        // At full scroll the thumb reaches the track's bottom (top + track - height).
        var area = new SKRect(10f, 100f, 300f, 400f); // height 300
        float maxScroll = 300f;
        var g = CalendarLayout.BuildAgendaScrollGeometry(area, maxScrollY: maxScroll, scrollY: maxScroll, scale: 1f);
        float trackHeight = area.Height - 8f;
        float expectedBottomY = area.Top + 4f + (trackHeight - g.ThumbHeight);
        Assert.AreEqual(expectedBottomY, g.ThumbY, 0.01f, "full offset parks the thumb at the track bottom");
    }

    [TestMethod]
    public void BuildAgendaScrollGeometry_ThumbHeight_ScalesWithContentRatio()
    {
        // More content (larger extent) shrinks the thumb toward the floor; the
        // thumb height is the larger of the 20*scale floor and the ratio-scaled
        // track share.
        var area = new SKRect(10f, 100f, 300f, 400f); // height 300
        float small = CalendarLayout.BuildAgendaScrollGeometry(area, maxScrollY: 300f, scrollY: 0f, scale: 1f).ThumbHeight;
        float large = CalendarLayout.BuildAgendaScrollGeometry(area, maxScrollY: 3000f, scrollY: 0f, scale: 1f).ThumbHeight;
        Assert.IsTrue(large < small, "a longer agenda yields a shorter thumb");
        Assert.IsTrue(small >= 20f, "the thumb never drops below the 20*scale floor");
    }
}
