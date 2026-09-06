namespace ModernWigiDash.Widgets;

/// <summary>
/// The calendar widget's hit geometry, computed once per frame from the
/// placement bounds, the uniform scale, and the display-facts row count -- the
/// same inputs the render path uses, so the drawn rows and the touch targets
/// can never drift apart. Render draws from this record and OnTouch hit-tests
/// the same record. The design-space constants (hero height, row height, gaps)
/// live only in <see cref="CalendarLayout.Compute"/>, never re-derived at a
/// draw site.
/// </summary>
public readonly record struct CalendarGeometry(
    SKRect HeroRect,
    IReadOnlyList<SKRect> RowRects,
    SKRect AllDayPillRect,
    float Pad,
    float RowHeight);

/// <summary>
/// Pure layout rules for the calendar widget: the design-space scale base and
/// the vertical stack (hero band, then up to three timed rows, then the all-day
/// pill). The stack top-aligns inside the bounds with equal side padding; each
/// element's height is a fixed design constant scaled by the frame's uniform
/// scale. A missing element (no hero, fewer rows than slots, no all-day items)
/// yields an empty rect that hit-testing treats as a miss.
/// </summary>
public static class CalendarLayout
{
    /// <summary>The design-space width the scale is derived from.</summary>
    public const float DesignWidth = 320f;

    /// <summary>The design-space height the scale is derived from.</summary>
    public const float DesignHeight = 240f;

    /// <summary>The hero band's design height.</summary>
    private const float HeroHeight = 76f;

    /// <summary>A timed row's design height.</summary>
    private const float TimedRowHeight = 44f;

    /// <summary>The gap between stacked elements, in design units.</summary>
    private const float StackGap = 12f;

    /// <summary>The all-day pill's design height.</summary>
    private const float PillHeight = 34f;

    /// <summary>The side/top padding, in design units.</summary>
    private const float PadDesign = 18f;

    /// <summary>
    /// Computes the calendar's hit geometry for one frame.
    /// </summary>
    /// <param name="bounds">The widget's placement bounds.</param>
    /// <param name="scale">The frame's uniform scale factor.</param>
    /// <param name="rowCount">How many timed rows the display carries (0-3).</param>
    /// <param name="hasHero">Whether a hero band is present.</param>
    /// <param name="hasAllDay">Whether the all-day pill is present.</param>
    public static CalendarGeometry Compute(SKRect bounds, float scale, int rowCount, bool hasHero, bool hasAllDay)
    {
        float pad = PadDesign * scale;
        float heroH = HeroHeight * scale;
        float rowH = TimedRowHeight * scale;
        float gap = StackGap * scale;
        float pillH = PillHeight * scale;

        float left = bounds.Left + pad;
        float right = bounds.Right - pad;

        var heroRect = hasHero
            ? new SKRect(left, bounds.Top + pad, right, bounds.Top + pad + heroH)
            : SKRect.Empty;

        // The timed rows stack below the hero (or from the top when there is no
        // hero), one gap between each.
        float y = hasHero ? heroRect.Bottom + gap : bounds.Top + pad;
        var rowRects = new List<SKRect>(Math.Max(0, rowCount));
        for (int i = 0; i < rowCount; i++)
        {
            rowRects.Add(new SKRect(left, y, right, y + rowH));
            y += rowH + gap;
        }

        // The all-day pill sits one gap below the last stacked element (`y`
        // already points past the last element plus its trailing gap); when
        // there is nothing above it, the pill starts at the top pad.
        float pillTop = (hasHero || rowCount > 0) ? y : bounds.Top + pad;
        var pillRect = hasAllDay
            ? new SKRect(left, pillTop, right, pillTop + pillH)
            : SKRect.Empty;

        return new CalendarGeometry(heroRect, rowRects, pillRect, pad, rowH);
    }

    /// <summary>
    /// Hit-tests a point against the geometry: returns the index of the timed
    /// row containing the point (-1 when none), or true via out when the point
    /// is on the hero or the all-day pill. The precedence is hero &gt; row &gt;
    /// pill (the hero band is the largest target and wins overlaps).
    /// </summary>
    /// <param name="geo">The frame's geometry record.</param>
    /// <param name="x">The touch X in canvas coordinates.</param>
    /// <param name="y">The touch Y in canvas coordinates.</param>
    /// <param name="onHero">Set true when the point is on the hero band.</param>
    /// <param name="onPill">Set true when the point is on the all-day pill.</param>
    /// <returns>The zero-based index of the contained timed row, or -1.</returns>
    public static int GetAction(CalendarGeometry geo, float x, float y, out bool onHero, out bool onPill)
    {
        onHero = false;
        onPill = false;
        if (!geo.HeroRect.IsEmpty && geo.HeroRect.Contains(x, y))
        {
            onHero = true;
            return -1;
        }

        for (int i = 0; i < geo.RowRects.Count; i++)
        {
            if (!geo.RowRects[i].IsEmpty && geo.RowRects[i].Contains(x, y))
                return i;
        }

        onPill = !geo.AllDayPillRect.IsEmpty && geo.AllDayPillRect.Contains(x, y);
        return -1;
    }
}
