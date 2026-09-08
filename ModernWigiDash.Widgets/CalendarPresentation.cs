namespace ModernWigiDash.Widgets;

/// <summary>One timed agenda row's display facts: the time string, the title,
/// the feed label (empty when the feed carried none), whether the event is
/// currently live (started, not ended), whether it is approaching (within the
/// urgency window), and the meeting link.</summary>
internal sealed record CalendarRow(
    string TimeText,
    string Title,
    string FeedLabel,
    bool IsLive,
    bool IsUrgent,
    string Url,
    DateTime Start = default,
    DateTime End = default);

/// <summary>The all-day strip's display facts: the label and the count of
/// all-day items in the window.</summary>
internal sealed record CalendarAllDayPill(string Label, int Count);

/// <summary>One cell in the mini month grid: the day number (0 = blank/other
/// month), whether it has events, whether it is today, whether it is the
/// currently-viewed day, and optional notable observance flag.</summary>
internal readonly record struct MonthCell(
    int Day,
    bool HasEvents,
    bool IsToday,
    bool IsViewed,
    bool IsCurrentMonth = true,
    bool IsNotable = false);

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
    bool HasData,
    IReadOnlyList<NotableDateItem>? NotableDates = null,
    CalendarRow? NextUpcomingEvent = null,
    string NextUpcomingCountdown = "",
    IReadOnlyList<CalendarRow>? TomorrowRows = null)
{
    /// <summary>Safe notable dates accessor.</summary>
    public IReadOnlyList<NotableDateItem> SafeNotableDates => NotableDates ?? [];

    /// <summary>Safe tomorrow rows accessor.</summary>
    public IReadOnlyList<CalendarRow> SafeTomorrowRows => TomorrowRows ?? [];
}

/// <summary>
/// Everything the calendar widget draws that is a *fact* about the snapshot at
/// a given instant: the date header (the viewed day's full name + month + day),
/// the mini month grid (with event dots and today/viewed highlights), the timed
/// rows for the viewed day, the all-day pill, and the unavailable-state hint.
/// </summary>
internal static class CalendarPresentation
{
    /// <summary>The urgency threshold: an event starting within this many
    /// minutes reads as "approaching" (amber).</summary>
    private const int UrgencyWindowMinutes = 30;

    /// <summary>
    /// Builds the widget's display facts from one snapshot for the viewed day
    /// at the current instant.
    /// </summary>
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

        var rows = new List<CalendarRow>();
        int slots = timedRows > CalendarFeedPolicy.MaxTimedRows
            ? timedRows
            : Math.Max(1, Math.Min(timedRows, CalendarFeedPolicy.MaxTimedRows));
        foreach (CalendarEvent e in dayTimed.Take(slots))
        {
            rows.Add(new CalendarRow(
                FormatTime(e.Start),
                Truncate(e.Title, 32),
                e.FeedLabel,
                IsLive(e, now),
                IsUrgent(e, now),
                e.Url,
                e.Start,
                e.End));
        }

        int allDaySlots = timedRows > CalendarFeedPolicy.MaxTimedRows ? int.MaxValue : 2;
        foreach (CalendarEvent e in dayAllDay.Take(allDaySlots))
        {
            rows.Add(new CalendarRow(
                "All day",
                Truncate(e.Title, 32),
                e.FeedLabel,
                false,
                false,
                e.Url,
                e.Start,
                e.End));
        }

        var pill = new CalendarAllDayPill(string.Empty, 0);

        // Month notable dates & holidays
        var notableDates = CalendarNotableDates.GetForMonth(viewDate.Year, viewDate.Month, snapshot.Events);
        var notableDays = notableDates.Select(n => n.Day).ToHashSet();

        // The mini month grid for the viewed day's month.
        var (grid, monthTitle) = BuildMonthGrid(viewDate, now, snapshot.Events, notableDays);

        // Next upcoming event across the entire schedule (row + its countdown).
        (CalendarRow? nextEvent, string nextCountdown) = BuildNextUpcoming(snapshot.Events, now);

        // Tomorrow's events.
        var tomorrowEvents = BuildTomorrowRows(snapshot.Events, now);

        return new CalendarDisplay(
            DateHeaderText: dateHeader,
            MonthGrid: grid,
            MonthTitle: monthTitle,
            Rows: rows,
            AllDayPill: pill,
            StalenessHint: string.Empty,
            HasData: true,
            NotableDates: notableDates,
            NextUpcomingEvent: nextEvent,
            NextUpcomingCountdown: nextCountdown,
            TomorrowRows: tomorrowEvents);
    }

    private static CalendarDisplay UnavailableDisplay(CalendarSnapshot? snapshot, DateTime now, DateTime viewDate)
    {
        string hint = snapshot is { HasData: false } && snapshot.LastUpdate != default
            ? "Last known schedule"
            : "No calendars configured";
        var notable = CalendarNotableDates.GetForMonth(viewDate.Year, viewDate.Month, []);
        var (grid, monthTitle) = BuildMonthGrid(viewDate, now, [], notable.Select(n => n.Day).ToHashSet());
        return new CalendarDisplay(
            DateHeaderText: FormatDateHeader(viewDate),
            MonthGrid: grid,
            MonthTitle: monthTitle,
            Rows: [],
            AllDayPill: new CalendarAllDayPill(string.Empty, 0),
            StalenessHint: hint,
            HasData: false,
            NotableDates: notable,
            NextUpcomingEvent: null,
            NextUpcomingCountdown: string.Empty,
            TomorrowRows: []);
    }

    private static readonly string[] MonthShortNames = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
    private static readonly string[] MonthFullNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    /// <summary>The one live predicate: an event that has started but not ended.</summary>
    private static bool IsLive(CalendarEvent e, DateTime now)
        => e.Start <= now && now < e.End;

    /// <summary>The one urgency predicate: a not-yet-live event starting within the
    /// urgency window reads as "approaching". Shared by the timed rows and the
    /// next-upcoming row so the two can never drift on what counts as urgent.</summary>
    private static bool IsUrgent(CalendarEvent e, DateTime now)
        => !IsLive(e, now) && (e.Start - now).TotalMinutes is > 0 and <= UrgencyWindowMinutes;

    /// <summary>An event still upcoming at <paramref name="current"/>: its end is
    /// in the future (or, for all-day items, its day has not passed).</summary>
    private static bool IsUpcoming(CalendarEvent ev, DateTime current)
    {
        if (ev.End != default)
            return ev.End > current;
        if (ev.IsAllDay)
            return ev.Start.Date >= current.Date;
        return ev.Start > current;
    }

    /// <summary>Builds the next-upcoming-event display fact: the first upcoming
    /// event's row plus its countdown string (empty when nothing is upcoming).
    /// The row carries the shared live/urgent predicates; the countdown is owned
    /// by <see cref="FormatCountdown"/>.</summary>
    private static (CalendarRow? Row, string Countdown) BuildNextUpcoming(IEnumerable<CalendarEvent> events, DateTime now)
    {
        CalendarEvent? upcoming = events
            .Where(e => IsUpcoming(e, now))
            .OrderBy(e => e.Start)
            .Cast<CalendarEvent?>()
            .FirstOrDefault();

        if (!upcoming.HasValue)
            return (null, string.Empty);

        CalendarEvent up = upcoming.Value;
        bool isLive = IsLive(up, now);
        var row = new CalendarRow(
            up.IsAllDay ? "All day" : FormatTime(up.Start),
            up.Title,
            up.FeedLabel,
            isLive,
            IsUrgent(up, now),
            up.Url,
            up.Start,
            up.End);
        return (row, isLive ? "Live now" : FormatCountdown(up, now));
    }

    /// <summary>The countdown string for an upcoming (not yet live) event: minutes
    /// under an hour, hours same-day, "Tomorrow" for the next day, else the month
    /// + day. One owner of the rule so the wording cannot drift between call sites.</summary>
    private static string FormatCountdown(CalendarEvent e, DateTime now)
    {
        var span = e.Start - now;
        if (span.TotalMinutes <= 60)
            return $"In {(int)Math.Max(1, span.TotalMinutes)}m";
        if (span.TotalHours < 24 && e.Start.Date == now.Date)
            return $"In {(int)span.TotalHours}h";
        if (e.Start.Date == now.Date.AddDays(1))
            return "Tomorrow";

        // For events further out, show a compact duration summary
        int days = (int)(e.Start.Date - now.Date).TotalDays;
        if (days < 30)
            return $"{days}d";
        if (days < 365)
            return $"{days / 7}w";
        return $"{days / 30}m";
    }

    /// <summary>Builds tomorrow's timed rows (up to three, ordered by start): the
    /// "Tomorrow" strip's display facts. All-day items read as "All day"; none are
    /// live or urgent (they are future by construction).</summary>
    private static IReadOnlyList<CalendarRow> BuildTomorrowRows(IEnumerable<CalendarEvent> events, DateTime now)
    {
        DateTime tomorrow = now.Date.AddDays(1);
        return events
            .Where(e => e.Start.Date == tomorrow)
            .OrderBy(e => e.Start)
            .Take(3)
            .Select(e => new CalendarRow(
                e.IsAllDay ? "All day" : FormatTime(e.Start),
                Truncate(e.Title, 32),
                e.FeedLabel,
                false,
                false,
                e.Url,
                e.Start,
                e.End))
            .ToList();
    }

    private static string FormatDateHeader(DateTime d)
        => $"{d.DayOfWeek}, {MonthShortNames[d.Month - 1]} {d.Day}";

    private static (IReadOnlyList<MonthCell> Grid, string Title) BuildMonthGrid(
        DateTime viewDate,
        DateTime now,
        IEnumerable<CalendarEvent> events,
        HashSet<int>? notableDays = null)
    {
        string title = $"{MonthFullNames[viewDate.Month - 1]} {viewDate.Year}";

        int daysInCurrentMonth = DateTime.DaysInMonth(viewDate.Year, viewDate.Month);
        DateTime firstOfMonth = new(viewDate.Year, viewDate.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        // Monday is column 0, Sunday is column 6 (matching M T W T F S S)
        int firstDow = ((int)firstOfMonth.DayOfWeek + 6) % 7;

        DateTime prevMonth = firstOfMonth.AddMonths(-1);
        int daysInPrevMonth = DateTime.DaysInMonth(prevMonth.Year, prevMonth.Month);

        HashSet<DateTime> eventDates = events
            .Where(e => e.Start.Month == viewDate.Month && e.Start.Year == viewDate.Year)
            .Select(e => e.Start.Date)
            .ToHashSet();

        var cells = new List<MonthCell>(35);
        for (int i = 0; i < 35; i++)
        {
            if (i < firstDow)
            {
                // Overflow from previous month
                int dayNum = daysInPrevMonth - firstDow + 1 + i;
                cells.Add(new MonthCell(dayNum, false, false, false, IsCurrentMonth: false, IsNotable: false));
            }
            else if (i < firstDow + daysInCurrentMonth)
            {
                // Day in current month
                int dayNum = i - firstDow + 1;
                DateTime cellDate = new(viewDate.Year, viewDate.Month, dayNum, 0, 0, 0, DateTimeKind.Unspecified);
                bool hasEvents = eventDates.Contains(cellDate);
                bool isToday = cellDate.Date == now.Date;
                bool isViewed = cellDate.Date == viewDate.Date;
                bool isNotable = notableDays != null && notableDays.Contains(dayNum);
                cells.Add(new MonthCell(dayNum, hasEvents, isToday, isViewed, IsCurrentMonth: true, IsNotable: isNotable));
            }
            else
            {
                // Overflow to next month
                int dayNum = i - (firstDow + daysInCurrentMonth) + 1;
                cells.Add(new MonthCell(dayNum, false, false, false, IsCurrentMonth: false, IsNotable: false));
            }
        }

        return (cells.AsReadOnly(), title);
    }

    private static string FormatTime(DateTime t)
        => $"{t.Hour:D2}:{t.Minute:D2}";

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "\u2026";
}
