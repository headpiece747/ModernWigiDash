using System.Reflection;
using ModernWigiDash.Sdk;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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

    /// <summary>Seeds the store, renders one frame (populating the widget's
    /// per-frame display + layout), and returns the widget plus the geometry so
    /// a test can aim a touch at a known zone.</summary>
    private static (CalendarWidget Widget, CalendarGeometry Geo) RenderWithEvents(params (string Title, string Url)[] events)
    {
        CalendarEventStore.Reset();
        List<CalendarEvent> evs = [];
        // First event is active (the hero); the rest are upcoming rows.
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

        var w = new CalendarWidget();
        var bounds = new SKRect(0, 0, 320, 240);
        using var surface = SKSurface.Create(new SKImageInfo(320, 240));
        w.Render(surface!.Canvas, bounds);
        float scale = Math.Min(bounds.Width / CalendarLayout.DesignWidth, bounds.Height / CalendarLayout.DesignHeight);
        bool hasHero = evs.Count > 0;
        // Mirror CalendarPresentation.Build's row count: the hero (row 0) plus up
        // to `slots` upcoming events after it.
        int slots = Math.Max(1, Math.Min(CalendarFeedPolicy.ResolveTimedRows(w.TimedRows), CalendarFeedPolicy.MaxTimedRows));
        int rowCount = hasHero ? 1 + Math.Min(slots, evs.Count - 1) : Math.Min(slots, evs.Count);
        var geo = CalendarLayout.Compute(bounds, scale, rowCount, hasHero, false);
        return (w, geo);
    }

    [TestCleanup]
    public void Cleanup() => CalendarEventStore.Reset();

    [TestMethod]
    public void OnTouch_Hero_OpensTheActiveEventUrl()
    {
        var (w, geo) = RenderWithEvents(("Standup", "https://meet.example/standup"), ("Lunch", "https://meet.example/lunch"));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        // Aim at the hero band's center.
        var p = new SKPoint(geo.HeroRect.MidX, geo.HeroRect.MidY);
        w.OnTouch(p, TouchEventType.TouchUp);

        CollectionAssert.AreEqual(new List<string> { "https://meet.example/standup" }, opened);
    }

    [TestMethod]
    public void OnTouch_TimedRow_OpensThatRowsUrl()
    {
        var (w, geo) = RenderWithEvents(("Standup", "https://meet.example/standup"), ("Lunch", "https://meet.example/lunch"));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        // Aim at the second row (index 1) center.
        var p = new SKPoint(geo.RowRects[1].MidX, geo.RowRects[1].MidY);
        w.OnTouch(p, TouchEventType.TouchUp);

        CollectionAssert.AreEqual(new List<string> { "https://meet.example/lunch" }, opened);
    }

    [TestMethod]
    public void OnTouch_RowWithoutUrl_IsANoOp()
    {
        var (w, geo) = RenderWithEvents(("Standup", ""), ("Lunch", ""));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        var p = new SKPoint(geo.RowRects[1].MidX, geo.RowRects[1].MidY);
        w.OnTouch(p, TouchEventType.TouchUp);

        Assert.AreEqual(0, opened.Count, "a blank link never reaches the shell-open seam");
    }

    [TestMethod]
    public void OnTouch_DownEvent_IgnoresNonRelease()
    {
        var (w, geo) = RenderWithEvents(("Standup", "https://meet.example/standup"));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        var p = new SKPoint(geo.HeroRect.MidX, geo.HeroRect.MidY);
        w.OnTouch(p, TouchEventType.TouchDown);

        Assert.AreEqual(0, opened.Count, "only a release opens a link");
    }

    [TestMethod]
    public void OpenMeetingLink_DisallowedScheme_IsRefusedNotThrown()
    {
        var (w, geo) = RenderWithEvents(("Standup", "file:///etc/passwd"));
        List<string> opened = [];
        w.OpenUrlSeam = opened.Add;

        // The hero carries the file: link; a release on it must be refused before
        // the seam runs (the widget has no context bound here, so the refusal log
        // is a null-tolerant no-op).
        w.OnTouch(new SKPoint(geo.HeroRect.MidX, geo.HeroRect.MidY), TouchEventType.TouchUp);

        Assert.AreEqual(0, opened.Count, "a file: scheme is refused before the seam runs");
    }
}
