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

    /// <summary>Enters detail mode the production way (a hero tap resolving the
    /// render-time event) and installs the detail frame facts (card rect + URL
    /// rect) so a subsequent release is interpreted against the geometry the last
    /// detail render drew.</summary>
    private static CalendarGestureState EnterDetailWithFacts(
        string title, string url, SKRect cardRect, SKRect urlRect, List<string> opened)
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

        // Install the detail frame facts the next render would hand over.
        gesture.SetDetailFrameFacts(cardRect, maxScrollY: 0f, urlRect);
        return gesture;
    }

    [TestMethod]
    public void DetailMode_TapInsideUrlRect_OpensLink()
    {
        // A release that lands inside the URL rect (and inside the card) routes
        // through the shell-open seam with the event's link -- it does NOT exit.
        var card = new SKRect(50, 100, 300, 400);
        var url = new SKRect(70, 360, 280, 390);
        List<string> opened = [];
        var gesture = EnterDetailWithFacts("Standup", "https://meet.example/standup", card, url, opened);

        var p = new SKPoint(url.MidX, url.MidY);
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
        var card = new SKRect(50, 100, 300, 400);
        var url = new SKRect(70, 360, 280, 390);
        List<string> opened = [];
        var gesture = EnterDetailWithFacts("Standup", "https://meet.example/standup", card, url, opened);

        // A point well inside the card body, clear of the URL rect and the header.
        var p = new SKPoint(card.MidX, 200f);
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
        var card = new SKRect(50, 100, 300, 400);
        var url = new SKRect(70, 360, 280, 390);
        List<string> opened = [];
        var gesture = EnterDetailWithFacts("Standup", "https://meet.example/standup", card, url, opened);

        var p = new SKPoint(card.MidX, 40f); // above card.Top (100)
        gesture.Feed(p, TouchEventType.TouchDown);
        gesture.Feed(p, TouchEventType.TouchUp);

        Assert.AreEqual(0, opened.Count, "a header-region tap never opens the link");
        Assert.IsNull(gesture.DetailEvent, "a header-region tap exits detail mode");
    }

    [TestCleanup]
    public void Cleanup() => CalendarEventStore.Reset();
}
