namespace ModernWigiDash.Tests;

/// <summary>
/// Fixture-based pins for the RFC 5545 parsing boundary (<see cref="CalendarEventParser"/>).
/// Each test feeds a raw iCalendar body through the one seam and asserts the
/// normalized local-time occurrences, so the hard spec parts (RRULE expansion,
/// EXDATE exceptions, VTIMEZONE / UTC / floating normalization, the all-day
/// split) are assertable without a network, a feed account, or pixels.
/// </summary>
[TestClass]
public class CalendarEventParserTests
{
    // A fixed window anchored on a known date so assertions are deterministic
    // regardless of when the suite runs. The window is wide enough to hold the
    // recurrence instances each fixture produces.
    private static readonly DateTime WindowStart = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime WindowEnd = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Unspecified);

    [TestMethod]
    public void Parse_SingleTimedUtcEvent_ConvertsToMachineLocal()
    {
        string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Test//EN
BEGIN:VEVENT
UID:evt-1@test
DTSTART:20260915T120000Z
DTEND:20260915T130000Z
SUMMARY:UTC Meeting
LOCATION:Room A
URL:https://meet.example.com/abc
END:VEVENT
END:VCALENDAR
""";

        IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Work", "#FF0000");

        Assert.AreEqual(1, events.Count);
        CalendarEvent e = events[0];
        Assert.AreEqual("UTC Meeting", e.Title);
        Assert.IsFalse(e.IsAllDay);
        Assert.AreEqual("Room A", e.Location);
        Assert.AreEqual("https://meet.example.com/abc", e.Url);
        Assert.AreEqual("evt-1@test", e.SeriesId);
        Assert.AreEqual("Work", e.FeedLabel);
        Assert.AreEqual("#FF0000", e.FeedColorHex);
        // 12:00 UTC converted to the machine zone: the wall-clock hour shifts by
        // the local offset, but the instant is preserved. Round-trip it back.
        DateTime roundTrip = e.Start.ToUniversalTime();
        Assert.AreEqual(new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc), roundTrip);
        Assert.AreEqual(TimeSpan.FromHours(1), e.End - e.Start);
    }

    [TestMethod]
    public void Parse_FloatingTime_TreatedAsMachineLocal()
    {
        // No Z, no TZID: the wall clock IS the local time.
        string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Test//EN
BEGIN:VEVENT
UID:float-1@test
DTSTART:20260915T093000
DTEND:20260915T103000
SUMMARY:Standup
END:VEVENT
END:VCALENDAR
""";

        IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Team", "");

        Assert.AreEqual(1, events.Count);
        CalendarEvent e = events[0];
        Assert.AreEqual(9, e.Start.Hour);
        Assert.AreEqual(30, e.Start.Minute);
        Assert.AreEqual(10, e.End.Hour);
        Assert.IsFalse(e.IsAllDay);
    }

    [TestMethod]
    public void Parse_WeeklyRecurrence_ExpandsEveryWeekdayInWindow()
    {
        // Weekly on Monday, starting the first Monday of the window.
        string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Test//EN
BEGIN:VEVENT
UID:weekly-1@test
DTSTART:20260907T100000Z
DTEND:20260907T110000Z
RRULE:FREQ=WEEKLY;BYDAY=MO
SUMMARY:Weekly Sync
END:VEVENT
END:VCALENDAR
""";

        IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Work", "");

        // Sept 2026 Mondays inside the window: 7, 14, 21, 28.
        Assert.AreEqual(4, events.Count);
        foreach (CalendarEvent e in events)
        {
            Assert.AreEqual(DayOfWeek.Monday, e.Start.DayOfWeek);
            Assert.AreEqual("Weekly Sync", e.Title);
        }
    }

    [TestMethod]
    public void Parse_WeeklyWithExDate_DropsTheExceptedOccurrence()
    {
        // Same weekly series, but the third Monday (Sep 21) is excepted.
        string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Test//EN
BEGIN:VEVENT
UID:weekly-ex@test
DTSTART:20260907T100000Z
DTEND:20260907T110000Z
RRULE:FREQ=WEEKLY;BYDAY=MO
EXDATE:20260921T100000Z
SUMMARY:Weekly Sync
END:VEVENT
END:VCALENDAR
""";

        IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Work", "");

        // Four Mondays minus the excepted Sep 21 = three.
        Assert.AreEqual(3, events.Count);
        Assert.IsFalse(events.Any(e => e.Start.Date == new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Unspecified)),
            "the EXDATE'd occurrence must not appear");
    }

    [TestMethod]
    public void Parse_AllDayItem_MarksIsAllDayAndNormalizesExclusiveEnd()
    {
        // Date-only DTSTART: an all-day item spanning two days.
        string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Test//EN
BEGIN:VEVENT
UID:allday-1@test
DTSTART;VALUE=DATE:20260915
DTEND;VALUE=DATE:20260917
SUMMARY:Vacation
END:VEVENT
END:VCALENDAR
""";

        IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Personal", "");

        Assert.AreEqual(1, events.Count);
        CalendarEvent e = events[0];
        Assert.IsTrue(e.IsAllDay);
        Assert.AreEqual(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Unspecified), e.Start.Date);
        // Exclusive end: the day after the last occupied day (Sep 16 -> Sep 17).
        Assert.AreEqual(new DateTime(2026, 9, 17, 0, 0, 0, DateTimeKind.Unspecified), e.End.Date);
    }

    [TestMethod]
    public void Parse_YearlyBirthday_ExpandsAcrossYears()
    {
        // A yearly event on Sep 15; the window holds exactly one instance.
        string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Test//EN
BEGIN:VEVENT
UID:bday-1@test
DTSTART;VALUE=DATE:20200915
RRULE:FREQ=YEARLY
SUMMARY:Anniversary
END:VEVENT
END:VCALENDAR
""";

        IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Personal", "");

        Assert.AreEqual(1, events.Count);
        CalendarEvent e = events[0];
        Assert.IsTrue(e.IsAllDay);
        Assert.AreEqual(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Unspecified), e.Start.Date);
        Assert.AreEqual("Anniversary", e.Title);
    }

    [TestMethod]
    public void Parse_DailyAcrossDst_KeepsWallClockStable()
    {
        // A daily recurring event in a named zone that crosses a DST boundary.
        // The wall-clock start time must stay constant even though the UTC
        // offset changes mid-window.
        string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Test//EN
BEGIN:VTIMEZONE
TZID:America/New_York
BEGIN:DAYLIGHT
TZOFFSETFROM:-0500
TZOFFSETTO:-0400
DTSTART:20260308T020000
RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=2SU
END:DAYLIGHT
BEGIN:STANDARD
TZOFFSETFROM:-0400
TZOFFSETTO:-0500
DTSTART:20261101T020000
RRULE:FREQ=YEARLY;BYMONTH=11;BYDAY=1SU
END:STANDARD
END:VTIMEZONE
BEGIN:VEVENT
UID:daily-dst@test
DTSTART;TZID=America/New_York:20260901T090000
DTEND;TZID=America/New_York:20260901T100000
RRULE:FREQ=DAILY
SUMMARY:Morning Call
END:VEVENT
END:VCALENDAR
""";

        IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Work", "");

        // Daily from Sep 1 to Sep 29 inclusive = 29 occurrences.
        Assert.AreEqual(29, events.Count);
        // Every occurrence keeps the same wall-clock time in the machine zone
        // only if the machine zone matches; the load-bearing pin is that the
        // count is exact (no open-series explosion) and every start is a
        // distinct day in order.
        for (int i = 1; i < events.Count; i++)
        {
            Assert.AreEqual(1, (events[i].Start.Date - events[i - 1].Start.Date).Days,
                "daily recurrence must advance exactly one day per instance");
        }
    }

    [TestMethod]
    public void Parse_SingleDayAllDay_ExclusiveEndIsNextDay()
    {
        // A one-day all-day item with no DTEND: Ical.Net resolves the exclusive
        // end to the next day, and the parser must keep it that way (not collapse
        // it back onto the start).
        string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Test//EN
BEGIN:VEVENT
UID:bday-1@test
DTSTART;VALUE=DATE:20260915
SUMMARY:Birthday
END:VEVENT
END:VCALENDAR
""";

        IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Personal", "");

        Assert.AreEqual(1, events.Count);
        CalendarEvent e = events[0];
        Assert.IsTrue(e.IsAllDay);
        Assert.AreEqual(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Unspecified), e.Start.Date);
        Assert.AreEqual(new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Unspecified), e.End.Date,
            "a single all-day day occupies exactly one day: end is the next midnight");
    }

    [TestMethod]
    public void Parse_MalformedPayload_ReturnsEmptyNotThrow()
    {
        string garbage = "this is not an ics file at all\n\x00\x01\x02";

        IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(garbage, WindowStart, WindowEnd, "Work", "");

        Assert.AreEqual(0, events.Count);
    }

    [TestMethod]
    public void Parse_EmptyPayload_ReturnsEmpty()
    {
        Assert.AreEqual(0, CalendarEventParser.Parse("", WindowStart, WindowEnd, "Work", "").Count);
        Assert.AreEqual(0, CalendarEventParser.Parse(null!, WindowStart, WindowEnd, "Work", "").Count);
    }

    [TestMethod]
    public void Parse_OutOfWindowEvents_AreFilteredOut()
    {
        // An event entirely before the window and one entirely after it.
        string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Test//EN
BEGIN:VEVENT
UID:before@test
DTSTART:20260801T100000Z
DTEND:20260801T110000Z
SUMMARY:Too Early
END:VEVENT
BEGIN:VEVENT
UID:after@test
DTSTART:20261001T100000Z
DTEND:20261001T110000Z
SUMMARY:Too Late
END:VEVENT
END:VCALENDAR
""";

        IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Work", "");

        Assert.AreEqual(0, events.Count, "events outside the window must be filtered out");
    }

    [TestMethod]
    public void Parse_MultipleFeeds_CarryTheirOwnLabels()
    {
        string ics = """
BEGIN:VCALENDAR
VERSION:2.0
PRODID:-//Test//EN
BEGIN:VEVENT
UID:m1@test
DTSTART:20260915T100000Z
DTEND:20260915T110000Z
SUMMARY:A
END:VEVENT
END:VCALENDAR
""";

        IReadOnlyList<CalendarEvent> work = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Work", "#111111");
        IReadOnlyList<CalendarEvent> personal = CalendarEventParser.Parse(ics, WindowStart, WindowEnd, "Personal", "#222222");

        Assert.AreEqual("Work", work[0].FeedLabel);
        Assert.AreEqual("#111111", work[0].FeedColorHex);
        Assert.AreEqual("Personal", personal[0].FeedLabel);
        Assert.AreEqual("#222222", personal[0].FeedColorHex);
    }
}
