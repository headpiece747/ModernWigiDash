using ModernWigiDash.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
    public void Build_ActiveEvent_HeroIsLiveWithElapsedCountdown()
    {
        var snap = new CalendarSnapshot
        {
            Events = [Ev("Standup", 14, 0, 15, 0)],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, 2);

        Assert.IsTrue(d.HasData);
        Assert.AreEqual("Standup", d.HeroTitle);
        Assert.IsTrue(d.HeroIsLive);
        // 14:30 now, started 14:00 -> +30m
        Assert.AreEqual("+30m", d.HeroCountdown);
        // The hero is also the first row.
        Assert.AreEqual(1, d.Rows.Count);
        Assert.AreEqual("14:00", d.Rows[0].TimeText);
    }

    [TestMethod]
    public void Build_NextEvent_HeroShowsRemainingCountdown()
    {
        var snap = new CalendarSnapshot
        {
            Events = [Ev("Lunch", 15, 0, 16, 0)],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, 2);

        Assert.IsFalse(d.HeroIsLive);
        Assert.AreEqual("in 30m", d.HeroCountdown);
    }

    [TestMethod]
    public void Build_MultiHour_RemainingUsesHoursAndMinutes()
    {
        var snap = new CalendarSnapshot
        {
            Events = [Ev("Workshop", 18, 0, 20, 0)],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, 2);

        // 14:30 -> 18:00 is 3h 30m
        Assert.AreEqual("in 3h 30m", d.HeroCountdown);
    }

    [TestMethod]
    public void Build_UrgentRow_TintsWithinThirtyMinutes()
    {
        // Hero at 14:00-15:00 (active at 14:30), a second event at 14:45 (15 min away) is urgent.
        var snap = new CalendarSnapshot
        {
            Events =
            [
                Ev("Active", 14, 0, 15, 0),
                Ev("Soon", 14, 45, 15, 0),
            ],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        };

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, 3);

        // Row 0 is the hero (active). Row 1 is "Soon" (urgent).
        Assert.AreEqual(2, d.Rows.Count);
        Assert.IsTrue(d.Rows[1].IsUrgent);
        Assert.AreEqual("Soon", d.Rows[1].Title);
    }

    [TestMethod]
    public void Build_AllDayItems_AggregateIntoPill()
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

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, 2);

        Assert.AreEqual(2, d.AllDayPill.Count);
        Assert.AreEqual("Birthday", d.AllDayPill.Label);
        // No timed rows (both are all-day).
        Assert.AreEqual(0, d.Rows.Count);
    }

    [TestMethod]
    public void Build_NoData_NeverFetched_ShowsEmptyHint()
    {
        CalendarDisplay d = CalendarPresentation.Build(null, Now, 2);

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

        CalendarDisplay d = CalendarPresentation.Build(snap, Now, 2);

        Assert.IsFalse(d.HasData);
        Assert.AreEqual("Last known schedule", d.StalenessHint);
    }
}

[TestClass]
public class CalendarLayoutTests
{
    private static readonly SKRect Bounds = new(0, 0, 320, 240);

    [TestMethod]
    public void Compute_StacksHeroRowsAndPill_TopAligned()
    {
        var geo = CalendarLayout.Compute(Bounds, 1f, rowCount: 2, hasHero: true, hasAllDay: true);

        Assert.IsFalse(geo.HeroRect.IsEmpty);
        Assert.AreEqual(2, geo.RowRects.Count);
        Assert.IsFalse(geo.AllDayPillRect.IsEmpty);

        // Each element starts below the previous one plus the gap.
        Assert.IsTrue(geo.RowRects[0].Top > geo.HeroRect.Bottom);
        Assert.IsTrue(geo.RowRects[1].Top > geo.RowRects[0].Bottom);
        Assert.IsTrue(geo.AllDayPillRect.Top > geo.RowRects[1].Bottom);
    }

    [TestMethod]
    public void Compute_NoHero_RowsStartAtTop()
    {
        var geo = CalendarLayout.Compute(Bounds, 1f, rowCount: 1, hasHero: false, hasAllDay: false);

        Assert.IsTrue(geo.HeroRect.IsEmpty);
        Assert.AreEqual(1, geo.RowRects.Count);
        Assert.AreEqual(Bounds.Top + geo.Pad, geo.RowRects[0].Top, 0.01f);
    }

    [TestMethod]
    public void GetAction_HeroBeatsRow_BeatsPill()
    {
        var geo = CalendarLayout.Compute(Bounds, 1f, rowCount: 1, hasHero: true, hasAllDay: true);

        // A point on the hero returns -1 with onHero set.
        Assert.AreEqual(-1, CalendarLayout.GetAction(geo, geo.HeroRect.MidX, geo.HeroRect.MidY, out bool hero, out _));
        Assert.IsTrue(hero);

        // A point on the row returns its index.
        Assert.AreEqual(0, CalendarLayout.GetAction(geo, geo.RowRects[0].MidX, geo.RowRects[0].MidY, out _, out _));

        // A point on the pill sets onPill.
        Assert.AreEqual(-1, CalendarLayout.GetAction(geo, geo.AllDayPillRect.MidX, geo.AllDayPillRect.MidY, out _, out bool pill));
        Assert.IsTrue(pill);
    }

    [TestMethod]
    public void GetAction_OffEverything_ReturnsMiss()
    {
        var geo = CalendarLayout.Compute(Bounds, 1f, rowCount: 0, hasHero: false, hasAllDay: false);

        Assert.AreEqual(-1, CalendarLayout.GetAction(geo, 5f, 5f, out bool hero, out bool pill));
        Assert.IsFalse(hero);
        Assert.IsFalse(pill);
    }
}
