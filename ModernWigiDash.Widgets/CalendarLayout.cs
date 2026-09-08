using System.Runtime.InteropServices;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The visual layout mode for the adaptive calendar widget.
/// </summary>
public enum CalendarViewMode
{
    /// <summary>Full canvas editorial layout (5x4, 1016x592): left vertical typographic strip, center poster month card, right live agenda panel.</summary>
    FullEditorial5x4,

    /// <summary>Wide split banner layout (4x2, 812x296): poster month card on left, upcoming agenda on right.</summary>
    SplitBanner4x2,

    /// <summary>Compact poster layout (2x3, 406x444): single-month poster card with high-contrast matrix and bottom upcoming event summary.</summary>
    CompactPoster2x3,

    /// <summary>Minimal 1x1 date card: color block with year, month, and day currently selected.</summary>
    MinimalDateCard1x1,
}

/// <summary>
/// The month-grid's intra-panel geometry, computed once per frame by
/// <see cref="CalendarLayout"/> so the draw path and the touch path share one
/// source of truth. Carries the weekday-header height and, for each of the 35
/// grid cells, its center (the day number's anchor), its event-dot center (the
/// small marker below the number), and its full rect. Before this record, the
/// renderer re-derived cell size, weekday-header height, circle radii, and dot
/// offsets on its own while the gesture state re-derived them again for
/// hit-testing -- three copies of the same numbers with no pin keeping them in
/// agreement. Now both consumers read the record the layout emitted.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MonthGridCell(SKPoint Center, SKPoint DotCenter, SKRect Rect);

/// <summary>The month-grid's per-frame geometry: the weekday-header height plus
/// the 35 cell records (empty when the grid is not drawn).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MonthGridGeometry(float WeekdayHeaderHeight, IReadOnlyList<MonthGridCell> Cells);

/// <summary>
/// The agenda panel's scrollbar thumb geometry, computed once from the scroll
/// area, the scrollable extent, and the current offset -- the one owner of the
/// thumb's height/ratio/position math so the render path draws it and any future
/// consumer reads the same source. <see cref="Visible"/> is false when there is
/// nothing to scroll (the extent is zero) or no scroll area; the renderer only
/// draws the thumb when it is true. Pure over its inputs: no pixels, no canvas.
/// </summary>
public readonly record struct AgendaScrollGeometry(bool Visible, float ThumbX, float ThumbY, float ThumbWidth, float ThumbHeight);

/// <summary>
/// The calendar widget's hit geometry, computed once per frame from the
/// placement bounds and the display-facts counts -- the same inputs the render
/// path uses, so the drawn rows and the touch targets can never drift apart.
/// Render draws from this record and OnTouch hit-tests the same record.
/// </summary>
public readonly record struct CalendarGeometry(
    SKRect HeaderRect,
    SKRect MonthGridRect,
    SKRect AllDayRect,
    IReadOnlyList<SKRect> RowRects,
    float TimeGutterWidth,
    float Pad,
    CalendarViewMode Mode = CalendarViewMode.CompactPoster2x3,
    SKRect LeftStripRect = default,
    SKRect MonthCardRect = default,
    SKRect AgendaRect = default,
    SKRect NotableDatesRect = default,
    SKRect PrevChevronRect = default,
    SKRect NextChevronRect = default,
    SKRect AgendaScrollAreaRect = default,
    MonthGridGeometry MonthGrid = default);

/// <summary>
/// Pure layout rules for the calendar widget, supporting an adaptive multi-view
/// architecture (5x4 Full Editorial, 4x2 Split Banner, 2x3 Compact Poster)
/// derived from widget bounds and canvas constraints.
/// </summary>
public static class CalendarLayout
{
    /// <summary>The design-space width the scale is derived from.</summary>
    public const float DesignWidth = 320f;

    /// <summary>The design-space height the scale is derived from.</summary>
    public const float DesignHeight = 240f;

    /// <summary>The top band's design height (header + month grid) in classic mode.</summary>
    private const float TopBandHeight = 80f;

    /// <summary>A timed row's design height in classic mode.</summary>
    private const float RowHeight = 44f;

    /// <summary>The gap between stacked elements, in design units.</summary>
    private const float StackGap = 10f;

    /// <summary>The all-day strip's design height in classic mode.</summary>
    private const float AllDayHeight = 30f;

    /// <summary>The side/top padding, in design units.</summary>
    public const float PadDesign = 14f;

    /// <summary>The time gutter's design width (the fixed left column for HH:mm).</summary>
    private const float TimeGutterDesign = 64f;

    /// <summary>The fraction of the top band width allocated to the date header
    /// (the rest goes to the month grid).</summary>
    private const float HeaderFraction = 0.42f;

    /// <summary>
    /// Resolves the adaptive view mode based on widget dimensions and user preference.
    /// </summary>
    /// <param name="width">The widget pixel width.</param>
    /// <param name="height">The widget pixel height.</param>
    /// <param name="layoutMode">Optional layout mode override ("Auto", "Poster Month", "Agenda", "Minimal Card").</param>
    /// <returns>The resolved view mode.</returns>
    public static CalendarViewMode ResolveViewMode(float width, float height, string? layoutMode = null)
    {
        if (width <= 260f && height <= 180f)
            return CalendarViewMode.MinimalDateCard1x1;

        if (string.Equals(layoutMode, "Minimal Card", StringComparison.OrdinalIgnoreCase))
            return CalendarViewMode.MinimalDateCard1x1;

        if (string.Equals(layoutMode, "Poster Month", StringComparison.OrdinalIgnoreCase))
            return CalendarViewMode.CompactPoster2x3;

        if (string.Equals(layoutMode, "Agenda", StringComparison.OrdinalIgnoreCase))
            return CalendarViewMode.SplitBanner4x2;

        if (width >= 880f && height >= 460f)
            return CalendarViewMode.FullEditorial5x4;

        if (width >= 560f)
            return CalendarViewMode.SplitBanner4x2;

        return CalendarViewMode.CompactPoster2x3;
    }

    /// <summary>
    /// Computes the calendar's hit geometry for one frame.
    /// </summary>
    /// <param name="bounds">The widget's placement bounds.</param>
    /// <param name="scale">The frame's uniform scale factor.</param>
    /// <param name="rowCount">How many timed rows the display carries (0-3).</param>
    /// <param name="hasAllDay">Whether the all-day strip is present.</param>
    /// <returns>The computed calendar geometry.</returns>
    public static CalendarGeometry Compute(SKRect bounds, float scale, int rowCount, bool hasAllDay)
        => Compute(bounds, scale, rowCount, hasAllDay, null);

    /// <summary>
    /// Computes the calendar's hit geometry for one frame with an optional layout override.
    /// </summary>
    /// <param name="bounds">The widget's placement bounds.</param>
    /// <param name="scale">The frame's uniform scale factor.</param>
    /// <param name="rowCount">How many timed rows the display carries (0-3).</param>
    /// <param name="hasAllDay">Whether the all-day strip is present.</param>
    /// <param name="layoutMode">Optional layout override.</param>
    /// <returns>The computed calendar geometry.</returns>
    public static CalendarGeometry Compute(SKRect bounds, float scale, int rowCount, bool hasAllDay, string? layoutMode)
    {
        CalendarViewMode mode = ResolveViewMode(bounds.Width, bounds.Height, layoutMode);

        // Classic mode fallback for small test harnesses or compact classic bounds (width < 560 and height < 280)
        if (mode == CalendarViewMode.CompactPoster2x3 && bounds.Height < 280f)
        {
            return ComputeClassic(bounds, scale, rowCount, hasAllDay);
        }

        return mode switch
        {
            CalendarViewMode.MinimalDateCard1x1 => ComputeMinimalDateCard1x1(bounds, scale),
            CalendarViewMode.FullEditorial5x4 => ComputeFullEditorial(bounds, scale, rowCount),
            CalendarViewMode.SplitBanner4x2 => ComputeSplitBanner(bounds, scale, rowCount),
            _ => ComputeCompactPoster(bounds, scale, rowCount),
        };
    }

    /// <summary>
    /// Builds the month-grid's intra-panel geometry from the grid rect and
    /// whether a weekday header row is drawn above the cells. This is the one
    /// spelling of the cell math (7 columns x 5 rows, the weekday-header height,
    /// the per-cell center and event-dot offset) that both the render path and
    /// the touch path read, so the numbers cannot drift between them. Returns an
    /// empty record when the grid rect is empty.
    /// </summary>
    private static MonthGridGeometry BuildMonthGrid(SKRect gridRect, float scale, bool hasWeekdayHeader)
    {
        if (gridRect.IsEmpty)
            return new MonthGridGeometry(0f, []);

        float weekdayH = hasWeekdayHeader ? 14f * scale : 0f;
        float cellW = gridRect.Width / 7f;
        float gridTop = gridRect.Top + weekdayH;
        float cellH = (gridRect.Height - weekdayH) / 5f;

        var cells = new List<MonthGridCell>(35);
        for (int i = 0; i < 35; i++)
        {
            int col = i % 7;
            int row = i / 7;
            SKPoint center = new(gridRect.Left + col * cellW + cellW / 2f, gridTop + row * cellH + cellH / 2f);
            // The event/notable dot sits below the day number (the renderer's
            // former cy + cellH * 0.32f offset), centered on the cell.
            SKPoint dotCenter = new(center.X, center.Y + cellH * 0.32f);
            SKRect cellRect = new(gridRect.Left + col * cellW, gridTop + row * cellH, gridRect.Left + (col + 1) * cellW, gridTop + (row + 1) * cellH);
            cells.Add(new MonthGridCell(center, dotCenter, cellRect));
        }
        return new MonthGridGeometry(weekdayH, cells);
    }

    private static CalendarGeometry ComputeMinimalDateCard1x1(SKRect bounds, float scale)
    {
        float pad = 8f * scale;
        float left = bounds.Left + pad;
        float right = bounds.Right - pad;
        float top = bounds.Top + pad;
        float bottom = bounds.Bottom - pad;

        var cardRect = new SKRect(left, top, right, bottom);
        return new CalendarGeometry(
            SKRect.Empty,
            SKRect.Empty,
            SKRect.Empty,
            [],
            0f,
            pad,
            CalendarViewMode.MinimalDateCard1x1,
            SKRect.Empty,
            cardRect,
            SKRect.Empty,
            SKRect.Empty,
            SKRect.Empty,
            SKRect.Empty,
            SKRect.Empty,
            new MonthGridGeometry(0f, []));
    }

    private static CalendarGeometry ComputeClassic(SKRect bounds, float scale, int rowCount, bool hasAllDay)
    {
        float pad = PadDesign * scale;
        float topH = TopBandHeight * scale;
        float rowH = RowHeight * scale;
        float gap = StackGap * scale;
        float allDayH = AllDayHeight * scale;
        float gutterW = TimeGutterDesign * scale;

        float left = bounds.Left + pad;
        float right = bounds.Right - pad;
        float top = bounds.Top + pad;

        var topBandRect = new SKRect(left, top, right, top + topH);
        float headerW = (right - left) * HeaderFraction;
        var headerRect = new SKRect(left, top, left + headerW, top + topH);
        float gridLeft = left + headerW + 6f * scale;
        var monthGridRect = new SKRect(gridLeft, top, right, top + topH);

        float y = topBandRect.Bottom + gap;
        var allDayRect = hasAllDay
            ? new SKRect(left, y, right, y + allDayH)
            : SKRect.Empty;
        if (hasAllDay)
            y += allDayH + gap;

        IReadOnlyList<SKRect> rowRects;
        if (rowCount > 0)
        {
            var list = new List<SKRect>(rowCount);
            for (int i = 0; i < rowCount; i++)
            {
                list.Add(new SKRect(left, y, right, y + rowH));
                y += rowH + gap;
            }
            rowRects = list;
        }
        else
        {
            rowRects = [];
        }

        return new CalendarGeometry(
            headerRect,
            monthGridRect,
            allDayRect,
            rowRects,
            gutterW,
            pad,
            CalendarViewMode.CompactPoster2x3,
            default,
            default,
            default,
            default,
            default,
            default,
            default,
            BuildMonthGrid(monthGridRect, scale, false));
    }

    private static CalendarGeometry ComputeFullEditorial(SKRect bounds, float scale, int rowCount)
    {
        float pad = 16f * scale;
        float gap = 12f * scale;
        float gutterW = TimeGutterDesign * scale;

        float left = bounds.Left + pad;
        float right = bounds.Right - pad;
        float top = bounds.Top + pad;
        float bottom = bounds.Bottom - pad;
        float availW = right - left;

        // Left vertical typography sidebar
        float stripW = Math.Max(70f, availW * 0.085f);
        var leftStripRect = new SKRect(left, top, left + stripW, bottom);

        // Remaining width split between center month card and right agenda
        float remW = right - (leftStripRect.Right + gap);
        float monthW = remW * 0.52f;
        var monthCardRect = new SKRect(leftStripRect.Right + gap, top, leftStripRect.Right + gap + monthW, bottom);
        var agendaRect = new SKRect(monthCardRect.Right + gap, top, right, bottom);

        // Center Month Card breakdown
        float cardPad = 12f * scale;
        float headerH = 36f * scale;
        var headerRect = new SKRect(monthCardRect.Left + cardPad, monthCardRect.Top + cardPad, monthCardRect.Right - cardPad, monthCardRect.Top + cardPad + headerH);

        // Chevrons inside header (right-aligned touch targets, vertically centered)
        float chevW = 22f * scale;
        float chevH = 22f * scale;
        float chevY = headerRect.Top + (headerH - chevH) / 2f;
        var nextChevronRect = new SKRect(headerRect.Right - chevW, chevY, headerRect.Right, chevY + chevH);
        var prevChevronRect = new SKRect(nextChevronRect.Left - chevW - 6f * scale, chevY, nextChevronRect.Left - 6f * scale, chevY + chevH);

        // Month Grid
        float gridTop = headerRect.Bottom + 6f * scale;
        float gridH = 115f * scale;
        var monthGridRect = new SKRect(monthCardRect.Left + cardPad, gridTop, monthCardRect.Right - cardPad, gridTop + gridH);

        // Notable Dates footer
        var notableDatesRect = new SKRect(monthCardRect.Left + cardPad, monthGridRect.Bottom + 6f * scale, monthCardRect.Right - cardPad, monthCardRect.Bottom - cardPad);

        // Right Agenda Panel breakdown: clear header height so hero card does not overlap title
        float agendaPad = 12f * scale;
        float agendaHeaderH = 32f * scale;
        float heroTop = agendaRect.Top + agendaPad + agendaHeaderH + 6f * scale;
        float heroH = 38f * scale;
        var allDayRect = new SKRect(agendaRect.Left + agendaPad, heroTop, agendaRect.Right - agendaPad, heroTop + heroH);

        // Scrollable agenda viewport
        float scrollAreaTop = allDayRect.Bottom + 6f * scale;
        var agendaScrollAreaRect = new SKRect(agendaRect.Left + agendaPad, scrollAreaTop, agendaRect.Right - agendaPad, agendaRect.Bottom - agendaPad);

        float rowY = scrollAreaTop + 2f * scale;
        float rowH = 24f * scale;
        float rowGap = 5f * scale;
        IReadOnlyList<SKRect> rowRects;
        if (rowCount > 0)
        {
            var list = new List<SKRect>(rowCount);
            for (int i = 0; i < rowCount; i++)
            {
                list.Add(new SKRect(agendaRect.Left + agendaPad, rowY, agendaRect.Right - agendaPad, rowY + rowH));
                rowY += rowH + rowGap;
            }
            rowRects = list;
        }
        else
        {
            rowRects = [];
        }

        return new CalendarGeometry(
            headerRect,
            monthGridRect,
            allDayRect,
            rowRects,
            gutterW,
            pad,
            CalendarViewMode.FullEditorial5x4,
            leftStripRect,
            monthCardRect,
            agendaRect,
            notableDatesRect,
            prevChevronRect,
            nextChevronRect,
            agendaScrollAreaRect,
            BuildMonthGrid(monthGridRect, scale, true));
    }

    private static CalendarGeometry ComputeSplitBanner(SKRect bounds, float scale, int rowCount)
    {
        float pad = 12f * scale;
        float gap = 10f * scale;
        float gutterW = TimeGutterDesign * scale;

        float left = bounds.Left + pad;
        float right = bounds.Right - pad;
        float top = bounds.Top + pad;
        float bottom = bounds.Bottom - pad;

        float availW = right - left;
        float monthW = availW * 0.44f;
        var monthCardRect = new SKRect(left, top, left + monthW, bottom);
        var agendaRect = new SKRect(monthCardRect.Right + gap, top, right, bottom);

        float cardPad = 10f * scale;
        float headerH = 36f * scale;
        var headerRect = new SKRect(monthCardRect.Left + cardPad, monthCardRect.Top + cardPad, monthCardRect.Right - cardPad, monthCardRect.Top + cardPad + headerH);

        float chevW = 18f * scale;
        float chevH = 18f * scale;
        var nextChevronRect = new SKRect(headerRect.Right - chevW, headerRect.Top + 6f * scale, headerRect.Right, headerRect.Top + 6f * scale + chevH);
        var prevChevronRect = new SKRect(nextChevronRect.Left - chevW - 4f * scale, headerRect.Top + 6f * scale, nextChevronRect.Left - 4f * scale, headerRect.Top + 6f * scale + chevH);

        float gridTop = headerRect.Bottom + 4f * scale;
        float gridH = 80f * scale;
        var monthGridRect = new SKRect(monthCardRect.Left + cardPad, gridTop, monthCardRect.Right - cardPad, gridTop + gridH);

        var notableDatesRect = new SKRect(monthCardRect.Left + cardPad, monthGridRect.Bottom + 4f * scale, monthCardRect.Right - cardPad, monthCardRect.Bottom - cardPad);

        float agendaPad = 10f * scale;
        float agendaHeaderH = 32f * scale;
        float scrollAreaTop = agendaRect.Top + agendaPad + agendaHeaderH + 2f * scale;
        var agendaScrollAreaRect = new SKRect(agendaRect.Left + agendaPad, scrollAreaTop, agendaRect.Right - agendaPad, agendaRect.Bottom - agendaPad);

        float rowY = scrollAreaTop + 2f * scale;
        float rowH = 34f * scale;
        float rowGap = 5f * scale;
        IReadOnlyList<SKRect> rowRects;
        if (rowCount > 0)
        {
            var list = new List<SKRect>(rowCount);
            for (int i = 0; i < rowCount; i++)
            {
                list.Add(new SKRect(agendaRect.Left + agendaPad, rowY, agendaRect.Right - agendaPad, rowY + rowH));
                rowY += rowH + rowGap;
            }
            rowRects = list;
        }
        else
        {
            rowRects = [];
        }

        return new CalendarGeometry(
            headerRect,
            monthGridRect,
            SKRect.Empty,
            rowRects,
            gutterW,
            pad,
            CalendarViewMode.SplitBanner4x2,
            SKRect.Empty,
            monthCardRect,
            agendaRect,
            notableDatesRect,
            prevChevronRect,
            nextChevronRect,
            agendaScrollAreaRect,
            BuildMonthGrid(monthGridRect, scale, true));
    }

    private static CalendarGeometry ComputeCompactPoster(SKRect bounds, float scale, int rowCount)
    {
        float pad = 12f * scale;
        float gutterW = TimeGutterDesign * scale;

        float left = bounds.Left + pad;
        float right = bounds.Right - pad;
        float top = bounds.Top + pad;
        float bottom = bounds.Bottom - pad;

        var monthCardRect = new SKRect(left, top, right, bottom);
        float cardPad = 10f * scale;
        float headerH = 40f * scale;
        var headerRect = new SKRect(monthCardRect.Left + cardPad, monthCardRect.Top + cardPad, monthCardRect.Right - cardPad, monthCardRect.Top + cardPad + headerH);

        var prevChevronRect = new SKRect(headerRect.Right - 32f * scale, headerRect.Top + 4f * scale, headerRect.Right - 18f * scale, headerRect.Top + 20f * scale);
        var nextChevronRect = new SKRect(headerRect.Right - 16f * scale, headerRect.Top + 4f * scale, headerRect.Right - 2f * scale, headerRect.Top + 20f * scale);

        float gridTop = headerRect.Bottom + 4f * scale;
        float gridH = 105f * scale;
        var monthGridRect = new SKRect(monthCardRect.Left + cardPad, gridTop, monthCardRect.Right - cardPad, gridTop + gridH);

        float footerBottom = bottom - cardPad;
        IReadOnlyList<SKRect> rowRects;
        if (rowCount > 0)
        {
            float rowH = 30f * scale;
            rowRects = [new SKRect(monthCardRect.Left + cardPad, footerBottom - rowH, monthCardRect.Right - cardPad, footerBottom)];
            footerBottom -= rowH + 4f * scale;
        }
        else
        {
            rowRects = [];
        }

        var notableDatesRect = new SKRect(monthCardRect.Left + cardPad, monthGridRect.Bottom + 4f * scale, monthCardRect.Right - cardPad, footerBottom);

        return new CalendarGeometry(
            headerRect,
            monthGridRect,
            SKRect.Empty,
            rowRects,
            gutterW,
            pad,
            CalendarViewMode.CompactPoster2x3,
            SKRect.Empty,
            monthCardRect,
            SKRect.Empty,
            notableDatesRect,
            prevChevronRect,
            nextChevronRect,
            SKRect.Empty,
            BuildMonthGrid(monthGridRect, scale, true));
    }

    /// <summary>
    /// Hit-tests a point against the geometry: returns the index of the timed
    /// row containing the point (-1 when none), or true via out when the point
    /// is on the all-day strip.
    /// </summary>
    /// <param name="geo">The frame's geometry record.</param>
    /// <param name="x">The touch X in canvas coordinates.</param>
    /// <param name="y">The touch Y in canvas coordinates.</param>
    /// <param name="onAllDay">Set true when the point is on the all-day strip.</param>
    /// <returns>The zero-based index of the contained timed row, or -1.</returns>
    public static int GetAction(CalendarGeometry geo, float x, float y, out bool onAllDay)
        => GetAction(geo, x, y, 0f, out onAllDay);

    /// <summary>
    /// Hit-tests a point against the geometry with an optional vertical scroll offset.
    /// </summary>
    /// <param name="geo">The frame's geometry record.</param>
    /// <param name="x">The touch X in canvas coordinates.</param>
    /// <param name="y">The touch Y in canvas coordinates.</param>
    /// <param name="scrollY">The vertical scroll offset of the agenda section.</param>
    /// <param name="onAllDay">Set true when the point is on the all-day strip.</param>
    /// <returns>The zero-based index of the contained timed row, or -1.</returns>
    public static int GetAction(CalendarGeometry geo, float x, float y, float scrollY, out bool onAllDay)
    {
        onAllDay = false;
        if (!geo.AgendaScrollAreaRect.IsEmpty)
        {
            if (geo.AgendaScrollAreaRect.Contains(x, y))
            {
                float scrolledY = y + scrollY;
                for (int i = 0; i < geo.RowRects.Count; i++)
                {
                    if (!geo.RowRects[i].IsEmpty && geo.RowRects[i].Contains(x, scrolledY))
                        return i;
                }
            }

            onAllDay = !geo.AllDayRect.IsEmpty && geo.AllDayRect.Contains(x, y);
            return -1;
        }

        for (int i = 0; i < geo.RowRects.Count; i++)
        {
            if (!geo.RowRects[i].IsEmpty && geo.RowRects[i].Contains(x, y))
                return i;
        }

        onAllDay = !geo.AllDayRect.IsEmpty && geo.AllDayRect.Contains(x, y);
        return -1;
    }

    /// <summary>
    /// Returns true if the point is within the previous month chevron touch target.
    /// </summary>
    public static bool IsPrevChevronHit(CalendarGeometry geo, float x, float y)
        => !geo.PrevChevronRect.IsEmpty && geo.PrevChevronRect.Contains(x, y);

    /// <summary>
    /// Returns true if the point is within the next month chevron touch target.
    /// </summary>
    public static bool IsNextChevronHit(CalendarGeometry geo, float x, float y)
        => !geo.NextChevronRect.IsEmpty && geo.NextChevronRect.Contains(x, y);

    /// <summary>
    /// Computes the agenda scrollbar thumb geometry from the scroll area, the
    /// scrollable extent, and the current offset. The one owner of the thumb's
    /// height/ratio/position math (the renderer used to inline it): a zero
    /// extent or an empty scroll area yields <see cref="AgendaScrollGeometry"/>
    /// with <c>Visible == false</c>, so the caller draws nothing. Pure over its
    /// inputs -- no canvas, no pixels -- so the geometry is assertable directly.
    /// </summary>
    public static AgendaScrollGeometry BuildAgendaScrollGeometry(SKRect scrollArea, float maxScrollY, float scrollY, float scale)
    {
        if (scrollArea.IsEmpty || maxScrollY <= 0f)
            return new AgendaScrollGeometry(false, 0f, 0f, 0f, 0f);

        float trackHeight = scrollArea.Height - 8f * scale;
        float thumbHeight = Math.Max(20f * scale, trackHeight * (scrollArea.Height / (scrollArea.Height + maxScrollY)));
        float ratio = scrollY / maxScrollY;
        float thumbY = scrollArea.Top + 4f * scale + ratio * (trackHeight - thumbHeight);
        float thumbX = scrollArea.Right - 5f * scale;
        return new AgendaScrollGeometry(true, thumbX, thumbY, 3f * scale, thumbHeight);
    }
}

