namespace ModernWigiDash.Widgets;

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
    float Pad);

/// <summary>
/// Pure layout rules for the calendar widget, modeled after a "Today" agenda
/// view with a mini month grid: a top band holding the date header (left) and
/// the 5×7 month grid (right), an optional all-day strip below it, then a
/// stack of timed-event rows. Each row has a fixed-width time gutter on the
/// left and the title area to its right. The active/now event is marked with a
/// left accent bar (drawn by the render path), not a separate hero band, so
/// rows stay evenly spaced and never overlap.
/// </summary>
public static class CalendarLayout
{
    /// <summary>The design-space width the scale is derived from.</summary>
    public const float DesignWidth = 320f;

    /// <summary>The design-space height the scale is derived from.</summary>
    public const float DesignHeight = 240f;

    /// <summary>The top band's design height (header + month grid).</summary>
    private const float TopBandHeight = 80f;

    /// <summary>A timed row's design height.</summary>
    private const float RowHeight = 44f;

    /// <summary>The gap between stacked elements, in design units.</summary>
    private const float StackGap = 10f;

    /// <summary>The all-day strip's design height.</summary>
    private const float AllDayHeight = 30f;

    /// <summary>The side/top padding, in design units.</summary>
    private const float PadDesign = 14f;

    /// <summary>The time gutter's design width (the fixed left column for HH:mm).</summary>
    private const float TimeGutterDesign = 64f;

    /// <summary>The fraction of the top band width allocated to the date header
    /// (the rest goes to the month grid).</summary>
    private const float HeaderFraction = 0.42f;

    /// <summary>
    /// Computes the calendar's hit geometry for one frame.
    /// </summary>
    /// <param name="bounds">The widget's placement bounds.</param>
    /// <param name="scale">The frame's uniform scale factor.</param>
    /// <param name="rowCount">How many timed rows the display carries (0-3).</param>
    /// <param name="hasAllDay">Whether the all-day strip is present.</param>
    public static CalendarGeometry Compute(SKRect bounds, float scale, int rowCount, bool hasAllDay)
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

        // The top band spans the full content width.
        var topBandRect = new SKRect(left, top, right, top + topH);

        // The date header occupies the left portion of the top band.
        float headerW = (right - left) * HeaderFraction;
        var headerRect = new SKRect(left, top, left + headerW, top + topH);

        // The month grid occupies the right portion of the top band.
        float gridLeft = left + headerW + 6f * scale;
        var monthGridRect = new SKRect(gridLeft, top, right, top + topH);

        // The all-day strip sits one gap below the top band (when present).
        float y = topBandRect.Bottom + gap;
        var allDayRect = hasAllDay
            ? new SKRect(left, y, right, y + allDayH)
            : SKRect.Empty;
        if (hasAllDay)
            y += allDayH + gap;

        // The timed rows stack below, one gap between each.
        var rowRects = new List<SKRect>(Math.Max(0, rowCount));
        for (int i = 0; i < rowCount; i++)
        {
            rowRects.Add(new SKRect(left, y, right, y + rowH));
            y += rowH + gap;
        }

        return new CalendarGeometry(headerRect, monthGridRect, allDayRect, rowRects, gutterW, pad);
    }

    /// <summary>
    /// Hit-tests a point against the geometry: returns the index of the timed
    /// row containing the point (-1 when none), or true via out when the point
    /// is on the all-day strip. The header and month grid are not tappable
    /// (swipe gestures handle day navigation at the widget level).
    /// </summary>
    /// <param name="geo">The frame's geometry record.</param>
    /// <param name="x">The touch X in canvas coordinates.</param>
    /// <param name="y">The touch Y in canvas coordinates.</param>
    /// <param name="onAllDay">Set true when the point is on the all-day strip.</param>
    /// <returns>The zero-based index of the contained timed row, or -1.</returns>
    public static int GetAction(CalendarGeometry geo, float x, float y, out bool onAllDay)
    {
        onAllDay = false;
        for (int i = 0; i < geo.RowRects.Count; i++)
        {
            if (!geo.RowRects[i].IsEmpty && geo.RowRects[i].Contains(x, y))
                return i;
        }

        onAllDay = !geo.AllDayRect.IsEmpty && geo.AllDayRect.Contains(x, y);
        return -1;
    }
}
