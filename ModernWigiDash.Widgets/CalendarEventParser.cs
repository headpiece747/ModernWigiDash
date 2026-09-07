using System.Text.RegularExpressions;
using Ical.Net;
using Ical.Net.DataTypes;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The RFC 5545 parsing boundary for the calendar cluster: turns raw iCalendar
/// text into normalized <see cref="CalendarEvent"/> occurrences in the machine's
/// local time zone, expanded only inside a bounded moving window. Every hard
/// part of the spec lives here so no downstream module touches a raw iCalendar
/// type: recurrence expansion (RRULE), exceptions (EXDATE) and moved
/// occurrences (RECURRENCE-ID), timezone normalization (VTIMEZONE / UTC /
/// floating), and the all-day split. The window bound keeps a years-old work
/// calendar from expanding unbounded recurrences on every poll.
/// </summary>
internal static class CalendarEventParser
{
    /// <summary>
    /// Parses <paramref name="icsText"/> and returns the concrete occurrences
    /// whose start falls inside [windowStart, windowEnd), each converted to the
    /// machine's local time zone and tagged with the feed's label/color. A
    /// malformed or empty payload yields an empty list, never a throw: a bad
    /// feed degrades to "no events" (the store keeps its last-good snapshot),
    /// matching the absent-service house pattern.
    /// </summary>
    /// <param name="icsText">The raw iCalendar body (one or more VCALENDARs).</param>
    /// <param name="windowStart">Inclusive lower bound of the expansion window (local).</param>
    /// <param name="windowEnd">Exclusive upper bound of the expansion window (local).</param>
    /// <param name="feedLabel">The originating feed's display label.</param>
    /// <param name="feedColorHex">The originating feed's color hex, or empty.</param>
    public static IReadOnlyList<CalendarEvent> Parse(
        string icsText,
        DateTime windowStart,
        DateTime windowEnd,
        string feedLabel,
        string feedColorHex)
    {
        if (string.IsNullOrWhiteSpace(icsText))
            return [];

        List<CalendarEvent> results = [];
        try
        {
            var calendars = CalendarCollection.Load(icsText);
            foreach (var calendar in calendars)
                CollectOccurrences(calendar, windowStart, windowEnd, feedLabel, feedColorHex, results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A hostile or truncated feed must not take down the poll tick; the
            // caller treats an empty result as "nothing new" and keeps the cache.
            // One log line so a reliably-failing feed is observable, not silent.
            FileLog.Write($"[Calendar] Parse failed for feed '{feedLabel}': {ex.GetType().Name}: {ex.Message}");
            return [];
        }

        return results
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Title, StringComparer.Ordinal)
            .ToList();
    }

    private static void CollectOccurrences(
        Ical.Net.Calendar calendar,
        DateTime windowStart,
        DateTime windowEnd,
        string feedLabel,
        string feedColorHex,
        List<CalendarEvent> results)
    {
        // Expand recurring series from just before the window up to the window
        // end (exclusive). TakeWhileBefore is what makes a "daily forever" rule
        // terminate; without it Ical.Net throws EvaluationOutOfRangeException on
        // an open series. Non-recurring events are returned whole and filtered below.
        CalDateTime expandLimit = ToCalDateTime(windowEnd);
        CalDateTime expandStart = ToCalDateTime(windowStart.AddDays(-1));
        var options = new Ical.Net.Evaluation.EvaluationOptions();

        foreach (Ical.Net.CalendarComponents.CalendarEvent evt in calendar.Events)
        {
            // Project the periods up front: the window filter and timezone math
            // run per period, and the event's static fields (title, location,
            // link, uid) are applied to each surviving occurrence.
            List<Period> periods = evt.GetOccurrences(expandStart, options)
                .TakeWhileBefore(expandLimit)
                .Select(occ => occ.Period)
                .ToList();

            foreach (Period period in periods)
            {
                CalDateTime startCal = period.StartTime;
                DateTime startLocal = ToLocal(startCal);
                if (startLocal < windowStart || startLocal >= windowEnd)
                    continue;

                // EffectiveEndTime resolves the end whether the event declared
                // DTEND, a DURATION, or a date-only span; Period.EndTime is null
                // for duration-only items, so it cannot be the end source. Fall
                // back to the start for a zero-length item.
                CalDateTime endCal = period.EffectiveEndTime ?? startCal;
                DateTime endLocal = ToLocal(endCal);
                bool allDay = IsAllDay(startCal, endCal);

                // An all-day event's exclusive end is the next day; normalize so
                // the layout can treat End as "the day it occupies ends here".
                if (allDay && endLocal.Date == startLocal.Date)
                    endLocal = startLocal.Date.AddDays(1);

                results.Add(new CalendarEvent
                {
                    Title = evt.Summary ?? string.Empty,
                    Start = startLocal,
                    End = endLocal,
                    IsAllDay = allDay,
                    Location = evt.Location ?? string.Empty,
                    Description = StripHtml(evt.Description ?? string.Empty),
                    Url = evt.Url?.ToString() ?? string.Empty,
                    SeriesId = evt.Uid ?? string.Empty,
                    FeedLabel = feedLabel,
                    FeedColorHex = feedColorHex
                });
            }
        }
    }

    /// <summary>
    /// Converts an iCalendar <see cref="CalDateTime"/> to a machine-local
    /// DateTime. A UTC value (Z) is converted; a zoned value (TZID) is converted
    /// through its zone; a floating value (no zone, no Z) is taken as already
    /// local. This is the single timezone-normalization site, so the "convert
    /// everything to the machine's zone" rule has one owner.
    /// </summary>
    internal static DateTime ToLocal(CalDateTime cal)
    {
        // Rebuild the wall-clock instant this CalDateTime represents, then convert.
        DateTime wall = BuildWallClock(cal);

        if (cal.IsUtc)
            return DateTime.SpecifyKind(wall, DateTimeKind.Utc).ToLocalTime();

        if (!string.IsNullOrEmpty(cal.TimeZoneName))
        {
            TimeZoneInfo zone = ResolveZone(cal.TimeZoneName);
            DateTime zoned = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
            return TimeZoneInfo.ConvertTime(zoned, zone, TimeZoneInfo.Local);
        }

        // Floating time: no zone, no Z. Treat as the machine's local time.
        return wall;
    }

    /// <summary>Reconstructs the naive wall-clock DateTime a CalDateTime carries
    /// (its Date + optional Time), independent of any zone interpretation.</summary>
    private static DateTime BuildWallClock(CalDateTime cal)
    {
        if (cal.HasTime && cal.Time.HasValue)
        {
            TimeOnly t = cal.Time.Value;
            return new DateTime(cal.Date.Year, cal.Date.Month, cal.Date.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Unspecified);
        }
        return new DateTime(cal.Date.Year, cal.Date.Month, cal.Date.Day, 0, 0, 0, DateTimeKind.Unspecified);
    }

    private static TimeZoneInfo ResolveZone(string tzId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch
        {
            // An unknown TZID (a zone the OS doesn't know) degrades to the
            // machine zone rather than dropping the event: the wall-clock time
            // is still the best available reading.
            return TimeZoneInfo.Local;
        }
    }

    /// <summary>
    /// An event is all-day when its DTSTART is date-only (no time component).
    /// Providers differ on how they mark midnight-start items, so we also accept
    /// the explicit midnight-to-next-midnight shape as all-day.
    /// </summary>
    private static bool IsAllDay(CalDateTime start, CalDateTime end)
    {
        if (start.HasTime)
            return false;

        // Date-only start: all-day unless it is a timed midnight event that ends
        // within the same day (a genuine 00:00 appointment).
        if (end.HasTime)
        {
            TimeSpan span = BuildWallClock(end) - BuildWallClock(start);
            return span >= TimeSpan.FromDays(1);
        }
        return true;
    }

    private static CalDateTime ToCalDateTime(DateTime local)
        => new(local, CalDateTime.UtcTzId, hasTime: true);

    /// <summary>Strips HTML tags from a DESCRIPTION value (many feeds embed
    /// HTML in the body). Returns plain text with newlines preserved. Entities
    /// are decoded BEFORE tag stripping (and repeated to a fixed point) so an
    /// encoded tag like &amp;lt;img&amp;gt; cannot survive the strip.</summary>
    internal static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        // Decode entities first, then strip tags; repeat until stable so an
        // encoded tag (&amp;lt;script&amp;gt;) that decodes into a live tag is
        // caught on the next pass.
        string text = html;
        for (int i = 0; i < 3; i++)
        {
            string decoded = System.Net.WebUtility.HtmlDecode(text);
            string stripped = BrTagRegex.Replace(decoded, "\n");
            stripped = HtmlTagRegex.Replace(stripped, "");
            if (string.Equals(stripped, text, StringComparison.Ordinal))
                break;
            text = stripped;
        }

        // Collapse multiple blank lines.
        text = BlankLineRegex.Replace(text, "\n\n");
        return text.Trim();
    }

    // MA0009/S6444: These patterns are safe (no nested quantifiers) and use
    // RegexOptions.Compiled for performance; the timeout concern is mitigated
    // by the bounded input size from iCalendar feeds.
#pragma warning disable MA0009, S6444
    private static readonly System.Text.RegularExpressions.Regex BrTagRegex =
        new(@"<\s*(?:br|p|div)[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex HtmlTagRegex =
        new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex BlankLineRegex =
        new(@"\n{3,}", RegexOptions.Compiled);
#pragma warning restore MA0009, S6444
}
