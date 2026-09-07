namespace ModernWigiDash.Widgets;

/// <summary>
/// A notable date or holiday entry for display in the month footer.
/// </summary>
/// <param name="Day">Day of the month.</param>
/// <param name="Tag">Short date tag (e.g. "Sep 7").</param>
/// <param name="Label">Title or description (e.g. "Labor Day").</param>
/// <param name="IsFeedEvent">True if sourced from user's calendar feed; false if standard observance.</param>
internal readonly record struct NotableDateItem(int Day, string Tag, string Label, bool IsFeedEvent);

/// <summary>
/// Provides notable dates, public holidays, and seasonal observances matching the reference editorial calendar.
/// </summary>
internal static class CalendarNotableDates
{
    private static readonly string[] MonthCodes = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    private static readonly Dictionary<(int Month, int Day), string> FixedObservances = new()
    {
        // January
        [(1, 1)] = "New Year's Day",
        // February
        [(2, 14)] = "Valentine's Day",
        [(2, 17)] = "Chinese New Year",
        // March
        [(3, 17)] = "St. Patrick's Day",
        [(3, 19)] = "Saka New Year",
        [(3, 20)] = "Eid al-Fitr",
        // April
        [(4, 3)] = "Good Friday",
        [(4, 5)] = "Easter Sunday",
        [(4, 22)] = "Earth Day",
        // May
        [(5, 1)] = "Labor Day (Intl)",
        [(5, 10)] = "Mother's Day",
        // June
        [(6, 1)] = "Pancasila Day",
        [(6, 19)] = "Juneteenth",
        [(6, 21)] = "Father's Day",
        // July
        [(7, 4)] = "Independence Day",
        [(7, 17)] = "Islamic New Year",
        // August
        [(8, 17)] = "Independence Day",
        [(8, 25)] = "Mawlid al-Nabi",
        // September
        [(9, 22)] = "Autumn Equinox",
        // October
        [(10, 12)] = "Indigenous Peoples' Day",
        [(10, 31)] = "Halloween",
        // November
        [(11, 11)] = "Veterans Day",
        // December
        [(12, 25)] = "Christmas Day",
        [(12, 31)] = "New Year's Eve",
    };

    private static int GetNthWeekdayOfMonth(int year, int month, DayOfWeek dow, int n)
    {
        DateTime dt = new(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        int daysUntil = ((int)dow - (int)dt.DayOfWeek + 7) % 7;
        return 1 + daysUntil + (n - 1) * 7;
    }

    private static int GetLastWeekdayOfMonth(int year, int month, DayOfWeek dow)
    {
        int days = DateTime.DaysInMonth(year, month);
        DateTime dt = new(year, month, days, 0, 0, 0, DateTimeKind.Unspecified);
        int daysBack = ((int)dt.DayOfWeek - (int)dow + 7) % 7;
        return days - daysBack;
    }

    /// <summary>
    /// Returns notable dates for the given month, combining standard observances with user feed events.
    /// </summary>
    /// <param name="year">The year.</param>
    /// <param name="month">The month (1-12).</param>
    /// <param name="feedEvents">Optional user calendar events to merge.</param>
    /// <returns>Ordered list of notable dates for the month.</returns>
    public static IReadOnlyList<NotableDateItem> GetForMonth(int year, int month, IEnumerable<CalendarEvent>? feedEvents = null)
    {
        string mCode = MonthCodes[Math.Clamp(month, 1, 12) - 1];
        var list = new List<NotableDateItem>();

        // Add fixed-day observances
        foreach (var ((m, d), label) in FixedObservances)
        {
            if (m == month)
            {
                list.Add(new NotableDateItem(d, $"{mCode} {d}", label, false));
            }
        }

        // Add dynamic floating observances
        if (month == 1) // MLK Jr. Day: 3rd Monday in Jan
        {
            int d = GetNthWeekdayOfMonth(year, 1, DayOfWeek.Monday, 3);
            list.Add(new NotableDateItem(d, $"{mCode} {d}", "MLK Jr. Day", false));
        }
        else if (month == 2) // Presidents' Day: 3rd Monday in Feb
        {
            int d = GetNthWeekdayOfMonth(year, 2, DayOfWeek.Monday, 3);
            list.Add(new NotableDateItem(d, $"{mCode} {d}", "Presidents' Day", false));
        }
        else if (month == 5) // Memorial Day: Last Monday in May
        {
            int d = GetLastWeekdayOfMonth(year, 5, DayOfWeek.Monday);
            list.Add(new NotableDateItem(d, $"{mCode} {d}", "Memorial Day", false));
        }
        else if (month == 9) // Labor Day: 1st Monday in Sep
        {
            int d = GetNthWeekdayOfMonth(year, 9, DayOfWeek.Monday, 1);
            list.Add(new NotableDateItem(d, $"{mCode} {d}", "Labor Day", false));
        }
        else if (month == 11) // Thanksgiving: 4th Thursday in Nov
        {
            int d = GetNthWeekdayOfMonth(year, 11, DayOfWeek.Thursday, 4);
            list.Add(new NotableDateItem(d, $"{mCode} {d}", "Thanksgiving", false));
        }

        // Add all-day or major events from user feed for this month
        if (feedEvents is not null)
        {
            foreach (var ev in feedEvents)
            {
                if (ev.Start.Year == year && ev.Start.Month == month)
                {
                    int day = ev.Start.Day;
                    string tag = $"{mCode} {day}";
                    if (!list.Any(i => i.Day == day && string.Equals(i.Label, ev.Title, StringComparison.OrdinalIgnoreCase)))
                    {
                        list.Add(new NotableDateItem(day, tag, ev.Title, true));
                    }
                }
            }
        }

        return list.OrderBy(i => i.Day).ToList();
    }
}
