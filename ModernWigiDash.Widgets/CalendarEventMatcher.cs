namespace ModernWigiDash.Widgets;

/// <summary>
/// The calendar row-to-event match rule: given a displayed row and the store's
/// snapshot, resolve the underlying <see cref="CalendarEvent"/> the row was
/// built from. This is the ONE spelling of the fuzzy match (exact title, exact
/// start+title, or a short title prefix) that the hero-card tap and the agenda
/// row tap both route through, so the prefix rule cannot drift between the two
/// call sites. A no-match returns <c>null</c>.
/// </summary>
internal static class CalendarEventMatcher
{
    /// <summary>The title-prefix length used when neither an exact title nor an
    /// exact start+title match resolves the row. One constant, one owner.</summary>
    internal const int TitlePrefixLength = 10;

    /// <summary>Resolves the event behind a displayed row. Returns <c>null</c>
    /// when the snapshot is absent, has no data, or no event matches the row.</summary>
    public static CalendarEvent? Match(CalendarSnapshot? snapshot, CalendarRow row)
    {
        if (snapshot is null || !snapshot.HasData)
            return null;

        // An all-day row carries "All day" as its time text; a timed row carries
        // "HH:mm". Every production CalendarRow carries a real Start (the
        // presentation layer always passes e.Start), so the default-Start case
        // is unreachable; guard against it by skipping the date-shape match and
        // falling through to the title-only match below.
        bool isAllDayRow = string.Equals(row.TimeText, "All day", StringComparison.Ordinal);
        bool hasStart = row.Start != default;
        DateTime targetDate = row.Start.Date;

        CalendarEvent? byShape = null;
        if (hasStart)
        {
            byShape = snapshot.Events.FirstOrDefault(e =>
                e.Start.Date == targetDate &&
                e.IsAllDay == isAllDayRow &&
                e.Title.StartsWith(TitlePrefix(row.Title), StringComparison.Ordinal));
        }
        if (byShape != default)
            return byShape;

        // Fallback: an exact title or a prefix match regardless of date/shape
        // (covers the compact poster's bottom row, which may show a next-upcoming
        // event from a different day than the viewed day).
        var byTitle = snapshot.Events.FirstOrDefault(e =>
            string.Equals(e.Title, row.Title, StringComparison.Ordinal) ||
            e.Title.StartsWith(TitlePrefix(row.Title), StringComparison.Ordinal));
        return byTitle != default ? byTitle : null;
    }

    private static string TitlePrefix(string title)
        => title.Length > TitlePrefixLength ? title[..TitlePrefixLength] : title;
}
