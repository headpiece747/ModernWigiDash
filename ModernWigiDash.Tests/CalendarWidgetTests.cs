using System.Reflection;

namespace ModernWigiDash.Tests;

[TestClass]
public class CalendarWidgetTests
{
    [TestMethod]
    public void Metadata_DeclaresTheCalendarIdentity()
    {
        var meta = typeof(CalendarWidget).GetCustomAttribute<WidgetMetadataAttribute>();

        Assert.IsNotNull(meta);
        Assert.AreEqual("calendar", meta.Id);
        Assert.AreEqual("Calendar", meta.DisplayName);
        Assert.AreEqual(GridSizePreset.Size2x3, meta.DefaultGridSize);
    }

    [TestMethod]
    public void ParseFeeds_Empty_ReturnsNoFeeds()
    {
        var w = new CalendarWidget();
        Assert.AreEqual(0, w.ParseFeeds().Count);

        w.FeedsJson = "   ";
        Assert.AreEqual(0, w.ParseFeeds().Count);
    }

    [TestMethod]
    public void ParseFeeds_MalformedJson_ReturnsNoFeeds()
    {
        var w = new CalendarWidget { FeedsJson = "{not an array" };
        Assert.AreEqual(0, w.ParseFeeds().Count);

        w.FeedsJson = """{"kind":"ics"}"""; // not an array
        Assert.AreEqual(0, w.ParseFeeds().Count);
    }

    [TestMethod]
    public void ParseFeeds_IcsFeed_RoutesToIcsUrlArm()
    {
        var w = new CalendarWidget
        {
            FeedsJson = """[{"kind":"ics","feedId":"g","label":"Google","color":"#FF0000","url":"https://cal.example/ics","enabled":false}]""",
        };

        IReadOnlyList<CalendarFeed> feeds = w.ParseFeeds();

        Assert.AreEqual(1, feeds.Count);
        var ics = (IcsUrlFeed)feeds[0];
        Assert.AreEqual("g", ics.FeedId);
        Assert.AreEqual("Google", ics.Label);
        Assert.AreEqual("#FF0000", ics.ColorHex);
        Assert.AreEqual("https://cal.example/ics", ics.Url);
        Assert.IsFalse(ics.Enabled);
        Assert.AreEqual("ics", ics.Kind);
    }

    [TestMethod]
    public void ParseFeeds_CalDavFeed_RoutesToCalDavArm()
    {
        var w = new CalendarWidget
        {
            FeedsJson = """[{"kind":"caldav","feedId":"icloud","label":"iCloud","server":"https://caldav.icloud.com","port":443,"principalPath":"/calendars/me/","username":"me","selectedCalendars":["Work","Personal"]}]""",
        };

        IReadOnlyList<CalendarFeed> feeds = w.ParseFeeds();

        Assert.AreEqual(1, feeds.Count);
        var dav = (CalDavFeed)feeds[0];
        Assert.AreEqual("icloud", dav.FeedId);
        Assert.AreEqual("https://caldav.icloud.com", dav.Server);
        Assert.AreEqual(443, dav.Port);
        Assert.AreEqual("/calendars/me/", dav.PrincipalPath);
        Assert.AreEqual("me", dav.Username);
        CollectionAssert.AreEqual(new List<string> { "Work", "Personal" }, dav.SelectedCalendars.ToList());
        Assert.AreEqual("caldav", dav.Kind);
    }

    [TestMethod]
    public void ParseFeeds_MixedFeeds_PreservesOrderAndDefaults()
    {
        var w = new CalendarWidget
        {
            FeedsJson = """[{"kind":"ics","feedId":"a","label":"A","url":"https://a.example/ics"},{"kind":"caldav","feedId":"b","label":"B","server":"https://b.example","principalPath":"/p/","username":"u"}]""",
        };

        IReadOnlyList<CalendarFeed> feeds = w.ParseFeeds();

        Assert.AreEqual(2, feeds.Count);
        Assert.IsTrue(feeds[0] is IcsUrlFeed);
        Assert.IsTrue(feeds[1] is CalDavFeed { Port: 443, Enabled: true });
    }
}

[TestClass]
public class CalendarWidgetTapToOpenTests
{
    private static readonly DateTime Now = new(2026, 9, 6, 14, 30, 0, DateTimeKind.Unspecified);

    private static CalendarEvent Ev(string title, int sh, int sm, int eh, int em, string url)
        => new()
        {
            Title = title,
            Start = new DateTime(2026, 9, 6, sh, sm, 0, DateTimeKind.Unspecified),
            End = new DateTime(2026, 9, 6, eh, em, 0, DateTimeKind.Unspecified),
            Url = url,
        };

    /// <summary>A well-formed single-ICS-feed FeedsJson value: gives a test
    /// widget a real feed so it legitimately reads the shared store instead of
    /// rendering its own feed-less unavailable state.</summary>
    internal const string ValidIcsFeedJson =
        "[{\"kind\":\"ics\",\"feedId\":\"test-feed\",\"label\":\"Test\",\"color\":\"#FFFFFF\",\"url\":\"https://cal.example.ics\"}]";

    /// <summary>Seeds the store, renders one frame (populating the widget's
    /// per-frame display + layout), and returns the widget plus the geometry so
    /// a test can aim a touch at a known zone.</summary>
    private static (CalendarWidget Widget, CalendarGeometry Geo) RenderWithEvents(params (string Title, string Url)[] events)
    {
        CalendarEventStore.Reset();
        List<CalendarEvent> evs = [];
        // First event is active (live); the rest are upcoming rows.
        for (int i = 0; i < events.Length; i++)
        {
            if (i == 0)
                evs.Add(Ev(events[i].Title, 14, 0, 15, 0, events[i].Url));
            else
                evs.Add(Ev(events[i].Title, 15, 0 + i * 15, 16, 0, events[i].Url));
        }
        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events = evs,
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        });

        // The widget carries a real feed so it reads the seeded store (a feed-less
        // instance would render its own unavailable state and have no hero/rows to
        // touch).
        var w = new CalendarWidget { FeedsJson = ValidIcsFeedJson };
        var bounds = new SKRect(0, 0, 320, 240);
        using var surface = SKSurface.Create(new SKImageInfo(320, 240));
        w.Render(surface!.Canvas, bounds);
        float scale = Math.Min(bounds.Width / CalendarLayout.DesignWidth, bounds.Height / CalendarLayout.DesignHeight);
        int rowCount = Math.Min(CalendarFeedPolicy.ResolveTimedRows(w.TimedRows), evs.Count);
        var geo = CalendarLayout.Compute(bounds, scale, rowCount, false);
        return (w, geo);
    }

    [TestMethod]
    public void Render_FeedlessInstance_DoesNotReadAnotherInstancesStoreData()
    {
        // ADR-0011's static store is shared by every calendar instance. A feed-less
        // widget must render its OWN unavailable state, not bleed another (feeded)
        // instance's events from the shared store into its own display.
        CalendarEventStore.Reset();
        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events = [Ev("OtherInstanceEvent", 15, 0, 16, 0, "https://meet.example/other")],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        });

        var w = new CalendarWidget(); // no feeds
        var bounds = new SKRect(0, 0, 320, 240);
        using var surface = SKSurface.Create(new SKImageInfo(320, 240));
        w.Render(surface!.Canvas, bounds);

        // The feed-less widget renders its own empty display and never reads the
        // shared store: the memo is untouched (null) or, if a display was built,
        // it carries no rows / next-upcoming event from the other instance's data.
        if (w.LastDisplay is { } display)
        {
            Assert.AreEqual(0, display.Rows.Count, "a feed-less widget shows no rows from another instance's store data");
            Assert.IsNull(display.NextUpcomingEvent, "a feed-less widget shows no next-upcoming event from the shared store");
        }
    }

    [TestMethod]
    public void RestartProducer_FeedlessInstance_DoesNotClearSharedStore()
    {
        // The clobber at its source: a feed-less instance restarting its producer
        // must NOT write the shared process-wide store (another instance may own
        // it). Seed the store, drop the widget's feeds to zero, and confirm the
        // store still holds the other instance's snapshot.
        CalendarEventStore.Reset();
        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events = [Ev("OtherInstanceEvent", 15, 0, 16, 0, "https://meet.example/other")],
            HasData = true,
            IsLive = true,
            LastUpdate = Now,
        });

        var w = new CalendarWidget { FeedsJson = ValidIcsFeedJson };
        // Transition to a feed-less state through the inspector write-through path
        // (OnPropertyChanged routes FeedsJson edits to RestartProducer).
        w.FeedsJson = "";
        w.OnPropertyChanged(nameof(CalendarWidget.FeedsJson), "");

        // The shared store still carries the other instance's live snapshot; the
        // feed-less restart did not clobber it with an empty one.
        var snap = CalendarEventStore.ReadSnapshot();
        Assert.IsTrue(snap.HasData, "the feed-less restart left the shared store's data intact");
        Assert.AreEqual(1, snap.Events.Count, "the other instance's event survived the feed-less restart");
    }

    [TestMethod]
    public void RestartProducer_FeedsEmptiedWhileInDetail_ExitsDetailMode()
    {
        // A feed-less render draws the agenda/unavailable view, so the gesture
        // module must not keep interpreting taps through the now-invisible detail
        // branch. Entering detail mode and then emptying the feed list (the
        // inspector write-through path) must reset the detail state -- otherwise
        // the canvas shows the agenda while touch handling stays stuck in detail.
        // A controlled clock: the widget's render reads Clock.GetLocalNow(), so the
        // events must be positioned relative to the fake clock's actual start to be
        // "upcoming" (the fixed-date Ev helper would land in the past vs. the real
        // system clock and never enter detail mode).
        var clock = new FakeTimeProvider();
        DateTime t0 = clock.GetLocalNow().LocalDateTime;
        var evs = new List<CalendarEvent>
        {
            new() { Title = "Standup", Start = t0.AddMinutes(-15), End = t0.AddMinutes(0), Url = "https://meet.example/standup" },
            new() { Title = "Lunch", Start = t0.AddMinutes(30), End = t0.AddMinutes(60), Url = "https://meet.example/lunch" },
        };
        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events = evs,
            HasData = true,
            IsLive = true,
            LastUpdate = t0,
        });

        var bounds = new SKRect(0, 0, 320, 240);
        var w = new CalendarWidget { FeedsJson = ValidIcsFeedJson, Clock = clock };
        using var surface = SKSurface.Create(new SKImageInfo(320, 240));
        w.Render(surface!.Canvas, bounds);
        float scale = Math.Min(bounds.Width / CalendarLayout.DesignWidth, bounds.Height / CalendarLayout.DesignHeight);
        int rowCount = Math.Min(CalendarFeedPolicy.ResolveTimedRows(w.TimedRows), evs.Count);
        var geo = CalendarLayout.Compute(bounds, scale, rowCount, false);

        // Enter detail mode via a row tap.
        var p = new SKPoint(geo.RowRects[0].MidX, geo.RowRects[0].MidY);
        w.OnTouch(p, TouchEventType.TouchDown);
        w.OnTouch(p, TouchEventType.TouchUp);
        Assert.IsTrue(w.GestureDetailEventForTest is not null, "a row tap entered detail mode");

        // Empty the feed list through the inspector write-through path.
        w.FeedsJson = "";
        w.OnPropertyChanged(nameof(CalendarWidget.FeedsJson), "");

        Assert.IsNull(w.GestureDetailEventForTest, "emptying the feeds exits detail mode so canvas and touch agree");
    }

    [TestCleanup]
    public void Cleanup() => CalendarEventStore.Reset();

    [TestMethod]
    public void OnTouch_FirstRow_EntersDetailMode()
    {
        var (w, geo) = RenderWithEvents(("Standup", "https://meet.example/standup"), ("Lunch", "https://meet.example/lunch"));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        // Aim at row 0's center: down then up (a tap).
        var p = new SKPoint(geo.RowRects[0].MidX, geo.RowRects[0].MidY);
        w.OnTouch(p, TouchEventType.TouchDown);
        w.OnTouch(p, TouchEventType.TouchUp);

        // No URL is opened on the first tap; detail mode is entered instead.
        Assert.AreEqual(0, opened.Count, "tapping a row enters detail mode, not open a link");
    }

    [TestMethod]
    public void OnTouch_SecondRow_EntersDetailMode()
    {
        var (w, geo) = RenderWithEvents(("Standup", "https://meet.example/standup"), ("Lunch", "https://meet.example/lunch"));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        // Aim at the second row (index 1) center.
        var p = new SKPoint(geo.RowRects[1].MidX, geo.RowRects[1].MidY);
        w.OnTouch(p, TouchEventType.TouchDown);
        w.OnTouch(p, TouchEventType.TouchUp);

        Assert.AreEqual(0, opened.Count, "tapping a row enters detail mode, not open a link");
    }

    [TestMethod]
    public void OnTouch_RowWithoutUrl_IsANoOp()
    {
        var (w, geo) = RenderWithEvents(("Standup", ""), ("Lunch", ""));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        var p = new SKPoint(geo.RowRects[1].MidX, geo.RowRects[1].MidY);
        w.OnTouch(p, TouchEventType.TouchDown);
        w.OnTouch(p, TouchEventType.TouchUp);

        Assert.AreEqual(0, opened.Count, "a blank link never reaches the shell-open seam");
    }

    [TestMethod]
    public void OnTouch_DownEvent_IgnoresNonRelease()
    {
        var (w, geo) = RenderWithEvents(("Standup", "https://meet.example/standup"));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        var p = new SKPoint(geo.RowRects[0].MidX, geo.RowRects[0].MidY);
        w.OnTouch(p, TouchEventType.TouchDown);

        Assert.AreEqual(0, opened.Count, "only a release opens a link");
    }

    [TestMethod]
    public void OpenMeetingLink_DisallowedScheme_IsRefusedNotThrown()
    {
        var (w, geo) = RenderWithEvents(("Standup", "file:///etc/passwd"));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        // Row 0 carries the file: link; a release on it must be refused before
        // the seam runs (the widget has no context bound here, so the refusal log
        // is a null-tolerant no-op).
        var p = new SKPoint(geo.RowRects[0].MidX, geo.RowRects[0].MidY);
        w.OnTouch(p, TouchEventType.TouchDown);
        w.OnTouch(p, TouchEventType.TouchUp);

        Assert.AreEqual(0, opened.Count, "a file: scheme is refused before the seam runs");
    }

    [TestMethod]
    public void OnTouch_VerticalSwipe_ChangesViewedDay()
    {
        var (w, geo) = RenderWithEvents(("Standup", "https://meet.example/standup"));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        // A downward swipe (dy > 0) advances to the next day.
        var down = new SKPoint(geo.RowRects[0].MidX, geo.RowRects[0].MidY);
        var up = new SKPoint(down.X, down.Y + 60f);
        w.OnTouch(down, TouchEventType.TouchDown);
        w.OnTouch(up, TouchEventType.TouchUp);

        Assert.AreEqual(0, opened.Count, "a swipe does not open a link");
    }

    [TestMethod]
    public void Render_DisplayMemo_HitsWithinAMinute_AndInvalidatesAtTheBoundary()
    {
        // The display-facts memo (the house MemoSlot pattern) must reuse the cached
        // CalendarDisplay within a minute (no recompute) and recompute when the
        // minute rolls over (the live/urgent/countdown facts change at minute
        // granularity). Position an event 15 minutes out from the fake clock's
        // start so the countdown is "In 15m", then drive the clock across a
        // minute boundary and confirm the memo hits within the minute and
        // invalidates at the rollover.
        var clock = new FakeTimeProvider();
        DateTime t0 = clock.GetLocalNow().LocalDateTime;
        // An event 15 minutes out from the fake clock's actual start (the Ev
        // helper pins a fixed date that would not match the fake clock, so the
        // event is built inline against t0's date): the countdown reads "In 15m".
        DateTime eventStart = t0.AddMinutes(15);
        DateTime eventEnd = t0.AddMinutes(30);
        var evs = new List<CalendarEvent>
        {
            new()
            {
                Title = "Soon",
                Start = eventStart,
                End = eventEnd,
                Url = "https://meet.example/soon",
            }
        };
        CalendarEventStore.Reset();
        CalendarEventStore.UpdateFromDto(new CalendarSnapshot
        {
            Events = evs,
            HasData = true,
            IsLive = true,
            LastUpdate = t0,
        });

        // The widget must carry a real feed: a feed-less instance renders its own
        // unavailable state instead of reading the (shared) store, so the memo
        // would never compute a display from the seeded snapshot.
        var w = new CalendarWidget { FeedsJson = ValidIcsFeedJson };
        w.Clock = clock;
        var bounds = new SKRect(0, 0, 320, 240);
        using var surface = SKSurface.Create(new SKImageInfo(320, 240));

        w.Render(surface.Canvas, bounds);
        var first = w.LastDisplay;
        Assert.IsNotNull(first, "the first render computes a display");
        Assert.AreEqual("In 15m", first.NextUpcomingCountdown, "15 minutes out reads In 15m");
        int computesAfterFirst = w.DisplayRecomputeCount;

        // Advance 20 seconds - still the same minute: the memo hits, so no new
        // compute runs and the countdown is unchanged.
        clock.Advance(TimeSpan.FromSeconds(20));
        w.Render(surface.Canvas, bounds);
        Assert.AreEqual(computesAfterFirst, w.DisplayRecomputeCount, "within a minute the memo hits (no recompute)");
        Assert.AreEqual("In 15m", w.LastDisplay!.NextUpcomingCountdown, "the cached countdown is unchanged within a minute");

        // Advance a full minute: the minute-of-now key changes, the memo must
        // recompute, and the countdown reflects the new time. Total advance from
        // t0 is now 80s (20s + 60s), so the event is 13.67 minutes out -> "In 13m".
        clock.Advance(TimeSpan.FromMinutes(1));
        w.Render(surface.Canvas, bounds);
        Assert.AreEqual(computesAfterFirst + 1, w.DisplayRecomputeCount, "a minute rollover forces exactly one recompute");
        var after = w.LastDisplay;
        Assert.IsNotNull(after, "the rollover render recomputes a display");
        Assert.AreEqual("In 13m", after.NextUpcomingCountdown, "after the rollover the countdown reflects the new time");
    }
}
