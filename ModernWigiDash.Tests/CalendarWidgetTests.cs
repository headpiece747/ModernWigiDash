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
