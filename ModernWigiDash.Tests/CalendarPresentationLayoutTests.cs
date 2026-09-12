namespace ModernWigiDash.Tests;

[TestClass]
public class CalendarPresentationTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 14, 30, 0, DateTimeKind.Unspecified);

    private static CalendarEvent Ev(string title, int startHour, int startMin, int endHour, int endMin, bool allDay = false)
        => new()
        {
            Title = title,
            Start = new DateTime(2026, 9, 6, startHour, startMin, 0, DateTimeKind.Unspecified),
            End = new DateTime(2026, 9, 6, endHour, endMin, 0, DateTimeKind.Unspecified),
            IsAllDay = allDay,
        };

    [TestMethod]
    public void Build_ActiveEvent_RowIsLive()
    {
        var snap = new CalendarSnapshot
        {
            Events = [Ev("Standup", 14, 0, 15, 0)],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, Now.Date, 2);

        Assert.IsTrue(d.HasData);
        // The date header shows today's day name + month + day.
        Assert.AreEqual("Sunday, Sep 6", d.DateHeaderText);
        // The active event is the first row, flagged live.
        Assert.AreEqual(1, d.Rows.Count);
        Assert.AreEqual("Standup", d.Rows[0].Title);
        Assert.IsTrue(d.Rows[0].IsLive);
        Assert.IsFalse(d.Rows[0].IsUrgent);
        Assert.AreEqual("14:00", d.Rows[0].TimeText);
    }

    [TestMethod]
    public void Build_NextEvent_RowIsNotLiveOrUrgent()
    {
        var snap = new CalendarSnapshot
        {
            Events = [Ev("Lunch", 18, 0, 19, 0)],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, Now.Date, 2);

        Assert.AreEqual(1, d.Rows.Count);
        Assert.IsFalse(d.Rows[0].IsLive);
        Assert.IsFalse(d.Rows[0].IsUrgent);
        Assert.AreEqual("Lunch", d.Rows[0].Title);
    }

    [TestMethod]
    public void Build_UrgentRow_FlaggedWithinThirtyMinutes()
    {
        // Event at 14:45 (15 min from now=14:30) is urgent.
        var snap = new CalendarSnapshot
        {
            Events = [Ev("Soon", 14, 45, 15, 0)],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.AreEqual(1, d.Rows.Count);
        Assert.IsTrue(d.Rows[0].IsUrgent);
        Assert.IsFalse(d.Rows[0].IsLive);
        Assert.AreEqual("Soon", d.Rows[0].Title);
    }

    [TestMethod]
    public void Build_MultipleEvents_OrdersByStart()
    {
        var snap = new CalendarSnapshot
        {
            Events =
            [
                Ev("Later", 17, 0, 18, 0),
                Ev("Sooner", 15, 0, 16, 0),
            ],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.AreEqual(2, d.Rows.Count);
        Assert.AreEqual("Sooner", d.Rows[0].Title);
        Assert.AreEqual("Later", d.Rows[1].Title);
    }

    [TestMethod]
    public void Build_AllDayItems_AppearInRows()
    {
        var snap = new CalendarSnapshot
        {
            Events =
            [
                Ev("Birthday", 0, 0, 0, 0, allDay: true),
                Ev("Holiday", 0, 0, 0, 0, allDay: true),
            ],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, Now.Date, 2);

        // All-day events appear as rows with "All day" time text.
        Assert.AreEqual(2, d.Rows.Count);
        Assert.AreEqual("All day", d.Rows[0].TimeText);
        Assert.AreEqual("Birthday", d.Rows[0].Title);
        Assert.AreEqual("All day", d.Rows[1].TimeText);
        Assert.AreEqual("Holiday", d.Rows[1].Title);
    }

    [TestMethod]
    public void Build_NoData_NeverFetched_ShowsEmptyHint()
    {
        CalendarDisplay d = CalendarPresentation.Build(null, Now, Now.Date, 2);

        Assert.IsFalse(d.HasData);
        Assert.AreEqual("No calendars configured", d.StalenessHint);
        Assert.AreEqual(0, d.Rows.Count);
    }

    [TestMethod]
    public void Build_NoData_WithLastUpdate_ShowsStaleHint()
    {
        var snap = new CalendarSnapshot
        {
            Events = [],
            HasData = false,
            IsLive = false,
            LastUpdate = Now.AddMinutes(-5),
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, Now.Date, 2);

        Assert.IsFalse(d.HasData);
        Assert.AreEqual("Last known schedule", d.StalenessHint);
    }

    [TestMethod]
    public void Build_ViewedDay_EventsFilteredToThatDay()
    {
        var tomorrow = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Unspecified);
        var snap = new CalendarSnapshot
        {
            Events =
            [
                new CalendarEvent { Title = "Meeting", Start = tomorrow, End = tomorrow.AddHours(1) },
                Ev("TodayEvent", 15, 0, 16, 0),
            ],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        // Viewing today: only today's events show.
        CalendarDisplay dToday = CalendarPresentation.Build(snap, Now, Now.Date, 2);
        Assert.AreEqual(1, dToday.Rows.Count);
        Assert.AreEqual("TodayEvent", dToday.Rows[0].Title);

        // Viewing tomorrow: only tomorrow's events show.
        CalendarDisplay dTomorrow = CalendarPresentation.Build(snap, Now, tomorrow.Date, 2);
        Assert.AreEqual(1, dTomorrow.Rows.Count);
        Assert.AreEqual("Meeting", dTomorrow.Rows[0].Title);
    }

    [TestMethod]
    public void Build_MonthGrid_Has35Cells()
    {
        var snap = new CalendarSnapshot
        {
            Events = [Ev("Today", 15, 0, 16, 0)],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, Now.Date, 2);

        Assert.AreEqual(35, d.MonthGrid.Count);
        Assert.AreEqual("September 2026", d.MonthTitle);
    }

    [TestMethod]
    public void Build_MonthGrid_TodayIsMarked()
    {
        var snap = new CalendarSnapshot
        {
            Events = [],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, Now.Date, 2);

        MonthCell today = d.MonthGrid.First(c => c.Day == 6);
        Assert.IsTrue(today.IsToday);
        Assert.IsTrue(today.IsViewed);
    }

    [TestMethod]
    public void Build_MonthGrid_EventDotOnDayWithEvents()
    {
        var snap = new CalendarSnapshot
        {
            Events = [Ev("Event", 15, 0, 16, 0)],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, Now.Date, 2);

        MonthCell cell6 = d.MonthGrid.First(c => c.Day == 6);
        Assert.IsTrue(cell6.HasEvents);
    }

    [TestMethod]
    public void Build_FeedLabel_CarriedOnRow()
    {
        var snap = new CalendarSnapshot
        {
            Events =
            [
                new CalendarEvent { Title = "Sync", Start = new DateTime(2026, 9, 6, 15, 0, 0, DateTimeKind.Unspecified), End = new DateTime(2026, 9, 6, 16, 0, 0, DateTimeKind.Unspecified), FeedLabel = "Work" },
            ],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, Now.Date, 2);

        Assert.AreEqual(1, d.Rows.Count);
        Assert.AreEqual("Work", d.Rows[0].FeedLabel);
    }

    // --- Next-upcoming countdown tiers (the FormatCountdown rule, pinned through Build) ---

    private static CalendarSnapshot SnapWith(params CalendarEvent[] events)
        => new() { Events = events, HasData = true, IsLive = true, LastUpdate = Now };

    [TestMethod]
    public void Build_NextUpcoming_UnderAnHour_CountsDownInMinutes()
    {
        // An event starting 20 minutes from now reads "In 20m".
        var snap = SnapWith(Ev("Soon", 14, 50, 15, 20));
        var d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.AreEqual("In 20m", d.NextUpcomingCountdown);
        Assert.IsFalse(d.NextUpcomingEvent!.IsLive);
    }

    [TestMethod]
    public void Build_NextUpcoming_SameDayLater_CountsDownInHours()
    {
        // An event starting 3 hours from now (same day) reads "In 3h".
        var snap = SnapWith(Ev("Later", 17, 30, 18, 0));
        var d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.AreEqual("In 3h", d.NextUpcomingCountdown);
    }

    [TestMethod]
    public void Build_NextUpcoming_Tomorrow_ReadsTomorrow()
    {
        // An event on the next calendar day reads "Tomorrow" when viewing today.
        var tomorrow = new CalendarEvent
        {
            Title = "Tmrw",
            Start = new DateTime(2026, 9, 7, 9, 0, 0, DateTimeKind.Unspecified),
            End = new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Unspecified),
        };
        var snap = SnapWith(tomorrow);
        // View today (the day before the event)
        var d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.AreEqual("Tomorrow", d.NextUpcomingCountdown);
    }

    [TestMethod]
    public void Build_NextUpcoming_LiveNow_ReadsLiveNow()
    {
        // The currently-active event is the next upcoming one and reads "Live now".
        var snap = SnapWith(Ev("Standup", 14, 0, 15, 0));
        var d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.AreEqual("Live now", d.NextUpcomingCountdown);
        Assert.IsTrue(d.NextUpcomingEvent!.IsLive);
    }

    [TestMethod]
    public void Build_NextUpcoming_UrgentWindow_FlagsUrgentNotLive()
    {
        // An event starting within the 30-minute urgency window is urgent, not live.
        var snap = SnapWith(Ev("Imminent", 14, 45, 15, 15));
        var d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.IsTrue(d.NextUpcomingEvent!.IsUrgent);
        Assert.IsFalse(d.NextUpcomingEvent.IsLive);
    }

    [TestMethod]
    public void Build_NextUpcoming_ExactUrgencyBoundary_IsInclusive()
    {
        // Exactly 30 minutes out (Now 14:30, start 15:00) is still urgent: the
        // window is inclusive (<= UrgencyWindowMinutes). A regression flipping
        // <= to < at the boundary would make this non-urgent and fail here.
        var snap = SnapWith(Ev("Edge", 15, 0, 15, 30));
        var d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.IsTrue(d.NextUpcomingEvent!.IsUrgent, "the 30-minute boundary is inclusive");
        Assert.IsFalse(d.NextUpcomingEvent.IsLive);
    }

    [TestMethod]
    public void Build_NextUpcoming_WeekTier_RoundsUpNotTruncates()
    {
        // 30 days out: the week tier must not read "4w" (truncating 30/7) -- it
        // reads "5w", so the compact summary never understates the distance and
        // the day->week boundary stays monotonic (29d then 5w, not 29d then 4w).
        var far = new CalendarEvent
        {
            Title = "Far",
            Start = Now.Date.AddDays(30),
            End = Now.Date.AddDays(30).AddHours(1),
        };
        var snap = SnapWith(far);
        var d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.AreEqual("5w", d.NextUpcomingCountdown);
    }

    [TestMethod]
    public void Build_NextUpcoming_MonthTier_RoundsUpNotTruncates()
    {
        // 365 days out: the month tier rounds up (365/30 -> 13m), never truncates
        // to a figure that understates the true distance.
        var far = new CalendarEvent
        {
            Title = "Year",
            Start = Now.Date.AddDays(365),
            End = Now.Date.AddDays(365).AddHours(1),
        };
        var snap = SnapWith(far);
        var d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.AreEqual("13m", d.NextUpcomingCountdown);
    }

    [TestMethod]
    public void Build_NoUpcomingEvents_EmptyCountdownAndNullRow()
    {
        // Only a past event: nothing upcoming, so no row and no countdown.
        var snap = SnapWith(Ev("Past", 9, 0, 10, 0));
        var d = CalendarPresentation.Build(snap, Now, Now.Date, 3);

        Assert.IsNull(d.NextUpcomingEvent);
        Assert.AreEqual(string.Empty, d.NextUpcomingCountdown);
    }
}

[TestClass]
public class CalendarLayoutTests
{
    private static readonly SKRect Bounds = new(0, 0, 320, 240);

    [TestMethod]
    public void Compute_StacksHeaderRowsAndAllDay_TopAligned()
    {
        var geo = CalendarLayout.Compute(Bounds, 1f, rowCount: 2, hasAllDay: true);

        Assert.IsFalse(geo.HeaderRect.IsEmpty);
        Assert.AreEqual(2, geo.RowRects.Count);
        Assert.IsFalse(geo.AllDayRect.IsEmpty);

        // Each element starts below the previous one plus the gap.
        Assert.IsTrue(geo.AllDayRect.Top > geo.HeaderRect.Bottom);
        Assert.IsTrue(geo.RowRects[0].Top > geo.AllDayRect.Bottom);
        Assert.IsTrue(geo.RowRects[1].Top > geo.RowRects[0].Bottom);
    }

    [TestMethod]
    public void Compute_NoAllDay_RowsStartBelowHeader()
    {
        var geo = CalendarLayout.Compute(Bounds, 1f, rowCount: 1, hasAllDay: false);

        Assert.IsTrue(geo.AllDayRect.IsEmpty);
        Assert.AreEqual(1, geo.RowRects.Count);
        // Rows start below the header + gap.
        Assert.IsTrue(geo.RowRects[0].Top > geo.HeaderRect.Bottom);
    }

    [TestMethod]
    public void GetAction_RowHit_ReturnsIndex()
    {
        var geo = CalendarLayout.Compute(Bounds, 1f, rowCount: 2, hasAllDay: true);

        // A point on row 0 returns index 0.
        Assert.AreEqual(0, CalendarLayout.GetAction(geo, geo.RowRects[0].MidX, geo.RowRects[0].MidY, out _));

        // A point on row 1 returns index 1.
        Assert.AreEqual(1, CalendarLayout.GetAction(geo, geo.RowRects[1].MidX, geo.RowRects[1].MidY, out _));
    }

    [TestMethod]
    public void GetAction_AllDayHit_SetsFlag()
    {
        var geo = CalendarLayout.Compute(Bounds, 1f, rowCount: 1, hasAllDay: true);

        Assert.AreEqual(-1, CalendarLayout.GetAction(geo, geo.AllDayRect.MidX, geo.AllDayRect.MidY, out bool onAllDay));
        Assert.IsTrue(onAllDay);
    }

    [TestMethod]
    public void GetAction_OffEverything_ReturnsMiss()
    {
        var geo = CalendarLayout.Compute(Bounds, 1f, rowCount: 0, hasAllDay: false);

        Assert.AreEqual(-1, CalendarLayout.GetAction(geo, 5f, 5f, out bool onAllDay));
        Assert.IsFalse(onAllDay);
    }

    [TestMethod]
    public void Compute_TimeGutterWidth_IsPositive()
    {
        var geo = CalendarLayout.Compute(Bounds, 1f, rowCount: 1, hasAllDay: false);

        Assert.IsTrue(geo.TimeGutterWidth > 0);
    }
}
