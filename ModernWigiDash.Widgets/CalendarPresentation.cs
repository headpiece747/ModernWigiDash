namespace ModernWigiDash.Widgets;

/// <summary>One timed agenda row's display facts: the time string, the title,
/// the feed label (empty when the feed carried none), whether the event is
/// currently live (started, not ended), whether it is approaching (within the
/// urgency window), and the meeting link.</summary>
internal sealed record CalendarRow(string TimeText, string Title, string FeedLabel, bool IsLive, bool IsUrgent, string Url);

/// <summary>The all-day strip's display facts: the label and the count of
/// all-day items in the window.</summary>
internal sealed record CalendarAllDayPill(string Label, int Count);

/// <summary>One cell in the mini month grid: the day number (0 = blank/other
/// month), whether it has events, whether it is today, and whether it is the
/// currently-viewed day.</summary>
internal readonly record struct MonthCell(int Day, bool HasEvents, bool IsToday, bool IsViewed);

/// <summary>
/// The calendar widget's complete display facts at one instant: the date header
/// text, the mini month grid (5 rows × 7 cols), the timed rows, the all-day
/// pill, the staleness hint (empty when live), and the data fact. The render
/// methods lay these out; the rules that produced them are assertable here
/// without pixels.
/// </summary>
internal sealed record CalendarDisplay(
    string DateHeaderText,
    IReadOnlyList<MonthCell> MonthGrid,
    string MonthTitle,
    IReadOnlyList<CalendarRow> Rows,
    CalendarAllDayPill AllDayPill,
    string StalenessHint,
    bool HasData);

/// <summary>
/// Everything the calendar widget draws that is a *fact* about the snapshot at
/// a given instant: the date header (the viewed day's full name + month + day),
/// the mini month grid (with event dots and today/viewed highlights), the timed
/// rows for the viewed day, the all-day pill, and the unavailable-state hint.
/// The render methods become thin adapters that lay these out -- the display
/// rules are assertable without pixels. The "now" accuracy rule lives here: the
/// live and urgent flags are recomputed from the injected clock on every build,
/// so a tick that runs a second late still shows the correct state. All number
/// formatting routes through <see cref="DisplayFormat"/> (the invariant-culture
/// contract).
/// </summary>
internal static class CalendarPresentation
{
    /// <summary>The urgency threshold: an event starting within this many
    /// minutes reads as "approaching" (amber).</summary>
    private const int UrgencyWindowMinutes = 30;

    /// <summary>
    /// Builds the widget's display facts from one snapshot for the viewed day
    /// at the current instant. The date header is the viewed day's full name +
    /// abbreviated month + day. The mini month grid shows the viewed day's
    /// month with event dots. The timed rows are the events ON the viewed day
    /// (up to <paramref name="timedRows"/>), each flagged live (started, not
    /// ended relative to `now`) or urgent (starting within the urgency window
    /// relative to `now`). A no-data snapshot yields the named unavailable
    /// display (ADR-0017).
    /// </summary>
    /// <param name="snapshot">The merged calendar snapshot (may be null/empty).</param>
    /// <param name="now">The current machine-local instant (for live/urgent
    /// flags and the "today" marker in the grid).</param>
    /// <param name="viewDate">The day being displayed (the user may have swiped
    /// away from today).</param>
    /// <param name="timedRows">How many timed rows to show (1-3, resolved
    /// through CalendarFeedPolicy).</param>
    public static CalendarDisplay Build(CalendarSnapshot? snapshot, DateTime now, DateTime viewDate, int timedRows)
    {
        if (snapshot is null || !snapshot.HasData)
            return UnavailableDisplay(snapshot, now, viewDate);

        // Filter events to the viewed day.
        List<CalendarEvent> dayTimed = snapshot.Events
            .Where(e => !e.IsAllDay && e.Start.Date == viewDate.Date)
            .OrderBy(e => e.Start)
            .ToList();
        List<CalendarEvent> dayAllDay = snapshot.Events
            .Where(e => e.IsAllDay && e.Start.Date == viewDate.Date)
            .OrderBy(e => e.Start)
            .ToList();

        // The date header: full day name + abbreviated month + day.
        string dateHeader = FormatDateHeader(viewDate);

        // Timed rows for the viewed day, followed by all-day rows. All-day
        // events get "All day" as their time text so they render in the same
        // row format and are individually tappable.
        var rows = new List<CalendarRow>();
        int slots = Math.Max(1, Math.Min(timedRows, CalendarFeedPolicy.MaxTimedRows));
        foreach (CalendarEvent e in dayTimed.Take(slots))
        {
            bool isLive = e.Start <= now && now < e.End;
            bool isUrgent = !isLive && (e.Start - now).TotalMinutes is > 0 and <= UrgencyWindowMinutes;
            rows.Add(new CalendarRow(
                FormatTime(e.Start),
                Truncate(e.Title, 32),
                e.FeedLabel,
                isLive,
                isUrgent,
                e.Url));
        }

        // All-day events ride the same row list so they are individually
        // tappable and visible. They sort after timed events.
        foreach (CalendarEvent e in dayAllDay.Take(2))
        {
            rows.Add(new CalendarRow(
                "All day",
                Truncate(e.Title, 32),
                e.FeedLabel,
                false,
                false,
                e.Url));
        }

        var pill = new CalendarAllDayPill(string.Empty, 0);

        // The mini month grid for the viewed day's month.
        var (grid, monthTitle) = BuildMonthGrid(viewDate, now, snapshot.Events);

        return new CalendarDisplay(
            DateHeaderText: dateHeader,
            MonthGrid: grid,
            MonthTitle: monthTitle,
            Rows: rows,
            AllDayPill: pill,
            StalenessHint: string.Empty,
            HasData: true);
    }

    // --- helpers -------------------------------------------------------------

    private static CalendarDisplay UnavailableDisplay(CalendarSnapshot? snapshot, DateTime now, DateTime viewDate)
    {
        string hint = snapshot is { HasData: false } && snapshot.LastUpdate != default
            ? "Last known schedule"
            : "No calendars configured";
        var (grid, monthTitle) = BuildMonthGrid(viewDate, now, []);
        return new CalendarDisplay(
            DateHeaderText: FormatDateHeader(viewDate),
            MonthGrid: grid,
            MonthTitle: monthTitle,
            Rows: [],
            AllDayPill: new CalendarAllDayPill(string.Empty, 0),
            StalenessHint: hint,
            HasData: false);
    }

    /// <summary>Formats the date header: "Sunday, Sep 6" (full day name,
    /// abbreviated month, day-of-month).</summary>
    private static string FormatDateHeader(DateTime d)
    {
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
        return $"{d.DayOfWeek}, {months[d.Month - 1]} {d.Day}";
    }

    /// <summary>Builds the 5×7 mini month grid for the viewed day's month.
    /// Week starts on Sunday. Cells outside the month have Day=0. A cell has
    /// HasEvents=true when any event falls on that date. IsToday marks the
    /// actual today; IsViewed marks the currently-displayed day.</summary>
    private static (IReadOnlyList<MonthCell> Grid, string Title) BuildMonthGrid(DateTime viewDate, DateTime now, IEnumerable<CalendarEvent> events)
    {
        string[] months = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
        string title = $"{months[viewDate.Month - 1]} {viewDate.Year}";

        int daysInMonth = DateTime.DaysInMonth(viewDate.Year, viewDate.Month);
        int firstDayOfWeek = new DateTime(viewDate.Year, viewDate.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).DayOfWeek switch
        {
            DayOfWeek.Sunday => 0,
            DayOfWeek.Monday => 1,
            DayOfWeek.Tuesday => 2,
            DayOfWeek.Wednesday => 3,
            DayOfWeek.Thursday => 4,
            DayOfWeek.Friday => 5,
            _ => 6,
        };

        // Collect the set of dates that have events (for dot markers).
        HashSet<DateTime> eventDates = events
            .Where(e => e.Start.Month == viewDate.Month && e.Start.Year == viewDate.Year)
            .Select(e => e.Start.Date)
            .ToHashSet();

        var cells = new List<MonthCell>(35);
        for (int i = 0; i < 35; i++)
        {
            int dayNum = i - firstDayOfWeek + 1;
            if (dayNum < 1 || dayNum > daysInMonth)
            {
                cells.Add(new MonthCell(0, false, false, false));
            }
            else
            {
                DateTime cellDate = new(viewDate.Year, viewDate.Month, dayNum, 0, 0, 0, DateTimeKind.Unspecified);
                bool hasEvents = eventDates.Contains(cellDate);
                bool isToday = cellDate.Date == now.Date;
                bool isViewed = cellDate.Date == viewDate.Date;
                cells.Add(new MonthCell(dayNum, hasEvents, isToday, isViewed));
            }
        }

        return (cells.AsReadOnly(), title);
    }

    private static string FormatTime(DateTime t)
        => $"{t.Hour:D2}:{t.Minute:D2}";

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "\u2026";
}
