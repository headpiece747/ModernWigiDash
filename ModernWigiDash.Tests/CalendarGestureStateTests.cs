namespace ModernWigiDash.Tests;

/// <summary>
/// The calendar gesture-state module pinned at its interface: driven directly
/// against a rendered frame's facts (the geometry, the display, and the event
/// list the canvas drew) without re-reading the global store per tap. The key
/// invariant is that a tapped row or hero event resolves against the event list
/// handed in at render time -- not a fresh store read -- so a poll landing
/// between render and tap cannot change what a tap resolves to.
/// </summary>
[TestClass]
public class CalendarGestureStateTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 14, 30, 0, DateTimeKind.Unspecified);

    private static CalendarEvent Ev(string title, int sh, int sm, string url)
        => new()
        {
            Title = title,
            Start = new DateTime(2026, 9, 6, sh, sm, 0, DateTimeKind.Unspecified),
            End = new DateTime(2026, 9, 6, sh + 1, sm, 0, DateTimeKind.Unspecified),
            Url = url,
        };

    /// <summary>Builds a FullEditorial frame's facts (geometry + display + the
    /// event list the display was built from) for a given event set.</summary>
    private static (CalendarGeometry Geo, CalendarDisplay Display, IReadOnlyList<CalendarEvent> Events) BuildFrame(params (string Title, string Url)[] events)
    {
        List<CalendarEvent> evs = [];
        for (int i = 0; i < events.Length; i++)
            evs.Add(Ev(events[i].Title, 14 + i, 0, events[i].Url));

        var snapshot = new CalendarSnapshot { Events = evs, HasData = true, IsLive = true, LastUpdate = Now };
        var bounds = new SKRect(0, 0, 1016, 592);
        float scale = Math.Min(bounds.Width / CalendarLayout.DesignWidth, bounds.Height / CalendarLayout.DesignHeight);
        CalendarDisplay display = CalendarPresentation.Build(snapshot, Now, Now.Date, 100);
        CalendarGeometry geo = CalendarLayout.Compute(bounds, scale, display.Rows.Count, false);
        return (geo, display, evs);
    }

    [TestMethod]
    public void HeroTap_ResolvesAgainstHandedInEvents_NotAStoreReRead()
    {
        // Seed the global store with a DIFFERENT event than the frame's, so a
        // store re-read would resolve the wrong event. The frame's facts carry
        // "Standup"; the store holds "Other". A hero tap must resolve "Standup"
        // (the render-time fact), proving the match runs against the handed-in
        // list, not the store.
        var (geo, display, events) = BuildFrame(("Standup", "https://meet.example/standup"), ("Lunch", "https://meet.example/lunch"));
        Assert.IsFalse(geo.AllDayRect.IsEmpty, "FullEditorial has a hero card to tap");
        Assert.IsNotNull(display.NextUpcomingEvent, "the display carries a next-upcoming event");

        CalendarEventStore.Reset();
        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events = [Ev("Other", 14, 0, "https://meet.example/other")],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        });

        var gesture = new CalendarGestureState();
        gesture.SetFrameFacts(geo, display, Now, events);
        gesture.UpdateScrollExtent();

        var p = new SKPoint(geo.AllDayRect.MidX, geo.AllDayRect.MidY);
        gesture.Feed(p, TouchEventType.TouchDown);
        gesture.Feed(p, TouchEventType.TouchUp);

        Assert.IsTrue(gesture.DetailEvent is not null, "a hero tap enters detail mode");
        Assert.AreEqual("Standup", gesture.DetailEvent.Value.Title, "the tap resolved the render-time event, not the store's different one");
    }

    [TestMethod]
    public void HeroTap_StoreEmptyAfterRender_StillResolves()
    {
        // Stronger form: empty the store after handing in the frame's facts. If
        // the module re-read the store it would find no events and resolve
        // nothing; resolving the hero event at all proves it matched against the
        // render-time list.
        var (geo, display, events) = BuildFrame(("Standup", "https://meet.example/standup"), ("Lunch", "https://meet.example/lunch"));
        CalendarEventStore.Reset();

        var gesture = new CalendarGestureState();
        gesture.SetFrameFacts(geo, display, Now, events);
        gesture.UpdateScrollExtent();

        var p = new SKPoint(geo.AllDayRect.MidX, geo.AllDayRect.MidY);
        gesture.Feed(p, TouchEventType.TouchDown);
        gesture.Feed(p, TouchEventType.TouchUp);

        Assert.IsTrue(gesture.DetailEvent is not null, "the hero tap resolved the render-time event even though the store is empty");
        Assert.AreEqual("Standup", gesture.DetailEvent.Value.Title);
    }

    /// <summary>The bounds the detail geometry is computed for: a 250x300 widget.</summary>
    private static readonly SKRect DetailBounds = new(50, 100, 300, 400);

    /// <summary>Counts whose content FITS the card, so the meeting link is on
    /// screen and inside the card rect - the shape the link-tap tests need.</summary>
    private static DetailLineCounts FittingCounts => new(
        TitleLines: 1,
        LocationLines: null,
        DescriptionLines: null,
        UrlLines: 1,
        HasFeedLabel: false,
        HasDuration: true);

    /// <summary>Counts whose content OVERFLOWS the card, so there is a real
    /// scroll extent to drag against.</summary>
    private static DetailLineCounts OverflowingCounts => new(
        TitleLines: 6,
        LocationLines: 2,
        DescriptionLines: 3,
        UrlLines: 1,
        HasFeedLabel: true,
        HasDuration: true);

    /// <summary>Enters detail mode the production way (a hero tap resolving the
    /// render-time event) and installs the detail geometry, returning both so a
    /// test can pick its tap point FROM the geometry the module hit-tests
    /// against.
    ///
    /// The geometry is computed by <see cref="CalendarLayout.ComputeDetail"/>, the
    /// one owner, rather than handed in as two hand-picked rects. A test that
    /// invents its own card and URL rects can only prove the module reads the
    /// rects it was given; computing them the way production does is what proves
    /// the module hit-tests the real geometry (2026-09-27).</summary>
    private static (CalendarGestureState Gesture, CalendarDetailGeometry Detail) EnterDetail(
        string title, string url, DetailLineCounts counts, List<string> opened)
    {
        var (geo, display, events) = BuildFrame((title, url));
        Assert.IsNotNull(display.NextUpcomingEvent, "the frame carries a next-upcoming event to tap");

        var gesture = new CalendarGestureState();
        gesture.OpenUrlSeam = opened.Add;
        gesture.SetFrameFacts(geo, display, Now, events);
        gesture.UpdateScrollExtent();

        // A hero tap enters detail mode for the render-time event.
        var hero = new SKPoint(geo.AllDayRect.MidX, geo.AllDayRect.MidY);
        gesture.Feed(hero, TouchEventType.TouchDown);
        gesture.Feed(hero, TouchEventType.TouchUp);
        Assert.IsTrue(gesture.DetailEvent is not null, "the hero tap entered detail mode");

        CalendarDetailGeometry detail = CalendarLayout.ComputeDetail(DetailBounds, 1f, counts, scrollY: 0f);
        gesture.SetDetailGeometry(detail);
        return (gesture, detail);
    }

    [TestMethod]
    public void DetailMode_TapInsideUrlRect_OpensLink()
    {
        // A release that lands inside the URL rect (and inside the card) routes
        // through the shell-open seam with the event's link -- it does NOT exit.
        List<string> opened = [];
        var (gesture, detail) = EnterDetail("Standup", "https://meet.example/standup", FittingCounts, opened);
        Assert.IsFalse(detail.UrlRect.IsEmpty, "the fitting layout must place the link inside the card");
        Assert.IsTrue(detail.CardRect.Contains(detail.UrlRect.MidX, detail.UrlRect.MidY), "the link must be on screen for this test to mean anything");

        var p = new SKPoint(detail.UrlRect.MidX, detail.UrlRect.MidY);
        gesture.Feed(p, TouchEventType.TouchDown);
        gesture.Feed(p, TouchEventType.TouchUp);

        Assert.AreEqual(1, opened.Count, "a tap on the URL hint opens the meeting link");
        Assert.AreEqual("https://meet.example/standup", opened[0]);
        Assert.IsTrue(gesture.DetailEvent is not null, "opening the link stays in detail mode");
    }

    [TestMethod]
    public void DetailMode_TapOnCardBody_ExitNotOpen()
    {
        // A release on the card body (inside the card, but outside the URL rect and
        // below the card top) exits detail mode and never touches the seam.
        List<string> opened = [];
        var (gesture, detail) = EnterDetail("Standup", "https://meet.example/standup", FittingCounts, opened);

        // A point well inside the card body: below the card top, clear of the link.
        float bodyY = detail.CardRect.Top + 20f;
        Assert.IsTrue(bodyY < detail.UrlRect.Top, "the body point must sit above the link for this test to mean anything");
        var p = new SKPoint(detail.CardRect.MidX, bodyY);
        gesture.Feed(p, TouchEventType.TouchDown);
        gesture.Feed(p, TouchEventType.TouchUp);

        Assert.AreEqual(0, opened.Count, "a card-body tap never opens the link");
        Assert.IsNull(gesture.DetailEvent, "a card-body tap exits detail mode");
    }

    [TestMethod]
    public void DetailMode_TapAboveCardTop_Exits()
    {
        // A release above the card (the "Tap to go back" header region) exits even
        // though the event carries a link.
        List<string> opened = [];
        var (gesture, detail) = EnterDetail("Standup", "https://meet.example/standup", FittingCounts, opened);

        var p = new SKPoint(detail.CardRect.MidX, detail.CardRect.Top - 60f);
        gesture.Feed(p, TouchEventType.TouchDown);
        gesture.Feed(p, TouchEventType.TouchUp);

        Assert.AreEqual(0, opened.Count, "a header-region tap never opens the link");
        Assert.IsNull(gesture.DetailEvent, "a header-region tap exits detail mode");
    }

    [TestMethod]
    public void DetailMode_DragToScroll_ScrollsAndSuppressesTap()
    {
        // A Down inside the detail card followed by a Move beyond the drag
        // tolerance scrolls the detail view; the subsequent Up must NOT be read
        // as a tap (no open, no exit) because the gesture was a completed drag.
        List<string> opened = [];
        var (gesture, detail) = EnterDetail("Standup", "https://meet.example/standup", OverflowingCounts, opened);

        // The extent is now DERIVED from the geometry rather than handed in, so
        // this asserts the real one is positive rather than a fed-in number.
        Assert.IsTrue(gesture.MaxDetailScrollY > 0f, "overflowing content must leave a scroll extent to drag against");
        Assert.AreEqual(detail.MaxScrollY, gesture.MaxDetailScrollY, "the extent the module reports is the geometry's");

        // Down inside the card arms the detail drag.
        var down = new SKPoint(detail.CardRect.MidX, detail.CardRect.Top + 100f);
        gesture.Feed(down, TouchEventType.TouchDown);

        // Drag up by 50 (beyond the 4-unit tolerance): pulling the content up
        // increases the scroll offset, so DetailScrollY moves from 0 toward max.
        var moved = new SKPoint(down.X, down.Y - 50f);
        gesture.Feed(moved, TouchEventType.TouchMove);
        Assert.AreEqual(50f, gesture.DetailScrollY, "an upward drag scrolls the detail view");

        // The release completes the drag: no tap action (no open, no exit).
        gesture.Feed(moved, TouchEventType.TouchUp);
        Assert.AreEqual(0, opened.Count, "a completed drag never opens the link");
        Assert.IsTrue(gesture.DetailEvent is not null, "a completed drag stays in detail mode");
    }

    [TestMethod]
    public void AgendaMode_DragToScroll_ScrollsTheAgenda()
    {
        // In the agenda view (not detail), a Down inside the scroll area followed
        // by a Move beyond the tolerance scrolls the agenda list.
        // Six rows starting at 08:00 (the Ev helper offsets each by an hour, so
        // the last lands at 13:00 -- well within a representable day).
        var (geo, display, events) = BuildFrame(
            ("A", "https://e/a"), ("B", "https://e/b"), ("C", "https://e/c"),
            ("D", "https://e/d"), ("E", "https://e/e"), ("F", "https://e/f"));
        var gesture = new CalendarGestureState();
        gesture.SetFrameFacts(geo, display, Now, events);
        gesture.UpdateScrollExtent();
        Assert.IsTrue(gesture.MaxAgendaScrollY > 0f, "enough rows to overflow the scroll area");

        // Down inside the scroll area arms the agenda drag.
        var area = geo.AgendaScrollAreaRect;
        var down = new SKPoint(area.MidX, area.Bottom - 10f);
        gesture.Feed(down, TouchEventType.TouchDown);

        // Drag up by 50 (beyond the 4-unit tolerance): pulling the content up
        // increases the scroll offset, so AgendaScrollY moves from 0 toward max.
        var moved = new SKPoint(down.X, down.Y - 50f);
        gesture.Feed(moved, TouchEventType.TouchMove);
        Assert.IsTrue(gesture.AgendaScrollY > 0f, "an upward drag scrolls the agenda list");
    }

    [TestCleanup]
    public void Cleanup() => CalendarEventStore.Reset();
}
