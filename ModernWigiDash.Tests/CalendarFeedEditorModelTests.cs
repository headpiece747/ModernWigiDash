using System.Reflection;
using ModernWigiDash.App.Inspector;

namespace ModernWigiDash.Tests;

[TestClass]
public class CalendarFeedEditorModelTests
{
    [TestMethod]
    public void Parse_EmptyOrMalformed_ReturnsNoDrafts()
    {
        Assert.AreEqual(0, CalendarFeedEditorModel.Parse(null).Count);
        Assert.AreEqual(0, CalendarFeedEditorModel.Parse("   ").Count);
        Assert.AreEqual(0, CalendarFeedEditorModel.Parse("{not json").Count);
        Assert.AreEqual(0, CalendarFeedEditorModel.Parse("""{"kind":"ics"}""").Count, "a non-array root yields no drafts");
    }

    [TestMethod]
    public void Parse_IcsFeed_PopulatesTheDraft()
    {
        string json = """[{"kind":"ics","feedId":"g","label":"Google","url":"https://cal.example/ics","enabled":false}]""";

        IReadOnlyList<CalendarFeedDraft> drafts = CalendarFeedEditorModel.Parse(json);

        Assert.AreEqual(1, drafts.Count);
        var d = drafts[0];
        Assert.AreEqual("ics", d.Kind);
        Assert.AreEqual("g", d.FeedId);
        Assert.AreEqual("Google", d.Label);
        Assert.AreEqual("https://cal.example/ics", d.Url);
        Assert.IsFalse(d.Enabled);
    }

    [TestMethod]
    public void Parse_CalDavFeed_PopulatesTheTriple()
    {
        string json = """[{"kind":"caldav","feedId":"icloud","label":"iCloud","server":"https://caldav.icloud.com","port":443,"principalPath":"/calendars/me/","username":"me"}]""";

        CalendarFeedDraft d = CalendarFeedEditorModel.Parse(json)[0];

        Assert.AreEqual("caldav", d.Kind);
        Assert.AreEqual("https://caldav.icloud.com", d.Server);
        Assert.AreEqual(443, d.Port);
        Assert.AreEqual("/calendars/me/", d.PrincipalPath);
        Assert.AreEqual("me", d.Username);
    }

    [TestMethod]
    public void Serialize_RoundTripsAValidList()
    {
        string original = """[{"kind":"ics","feedId":"g","label":"Google","url":"https://cal.example/ics","enabled":true},{"kind":"caldav","feedId":"icloud","label":"iCloud","server":"https://caldav.icloud.com","port":443,"principalPath":"/p/","username":"me","enabled":true}]""";

        string serialized = CalendarFeedEditorModel.Serialize(CalendarFeedEditorModel.Parse(original));
        IReadOnlyList<CalendarFeedDraft> reparsed = CalendarFeedEditorModel.Parse(serialized);
        IReadOnlyList<CalendarFeedDraft> originalParsed = CalendarFeedEditorModel.Parse(original);

        Assert.AreEqual(originalParsed.Count, reparsed.Count, "parse -> serialize -> parse keeps the same feed count");
        for (int i = 0; i < originalParsed.Count; i++)
        {
            Assert.AreEqual(originalParsed[i].Kind, reparsed[i].Kind, $"feed {i} kind");
            Assert.AreEqual(originalParsed[i].FeedId, reparsed[i].FeedId, $"feed {i} id");
            Assert.AreEqual(originalParsed[i].Label, reparsed[i].Label, $"feed {i} label");
            Assert.AreEqual(originalParsed[i].Url, reparsed[i].Url, $"feed {i} url");
            Assert.AreEqual(originalParsed[i].Server, reparsed[i].Server, $"feed {i} server");
            Assert.AreEqual(originalParsed[i].Port, reparsed[i].Port, $"feed {i} port");
            Assert.AreEqual(originalParsed[i].PrincipalPath, reparsed[i].PrincipalPath, $"feed {i} principal");
            Assert.AreEqual(originalParsed[i].Username, reparsed[i].Username, $"feed {i} username");
            Assert.AreEqual(originalParsed[i].Enabled, reparsed[i].Enabled, $"feed {i} enabled");
        }
    }

    [TestMethod]
    public void Serialize_DropsIncompleteFeeds()
    {
        var drafts = new List<CalendarFeedDraft>
        {
            new() { Kind = "ics", FeedId = "ok", Label = "Ok", Url = "https://ok.example/ics" },
            new() { Kind = "ics", FeedId = "bad", Label = "Bad", Url = "not-a-url" },
            new() { Kind = "caldav", FeedId = "nouser", Server = "https://dav.example", Username = "" },
        };

        string serialized = CalendarFeedEditorModel.Serialize(drafts);

        // Only the complete ics feed survives.
        Assert.AreEqual(1, CalendarFeedEditorModel.Parse(serialized).Count);
        Assert.AreEqual("ok", CalendarFeedEditorModel.Parse(serialized)[0].FeedId);
    }

    [TestMethod]
    public void Serialize_AllIncomplete_YieldsEmptyArray()
    {
        var drafts = new List<CalendarFeedDraft> { new() { Kind = "ics", Url = "" } };

        Assert.AreEqual("[]", CalendarFeedEditorModel.Serialize(drafts));
    }

    [TestMethod]
    public void IsComplete_IcsRequiresHttpUrl()
    {
        Assert.IsTrue(CalendarFeedEditorModel.IsComplete(new CalendarFeedDraft { Kind = "ics", Url = "https://x.example/ics" }));
        Assert.IsTrue(CalendarFeedEditorModel.IsComplete(new CalendarFeedDraft { Kind = "ics", Url = "http://x.example/ics" }));
        Assert.IsFalse(CalendarFeedEditorModel.IsComplete(new CalendarFeedDraft { Kind = "ics", Url = "file:///etc/passwd" }), "a non-http(s) URL is incomplete");
        Assert.IsFalse(CalendarFeedEditorModel.IsComplete(new CalendarFeedDraft { Kind = "ics", Url = "" }));
    }

    [TestMethod]
    public void IsComplete_CalDavRequiresServerAndUsername()
    {
        Assert.IsTrue(CalendarFeedEditorModel.IsComplete(new CalendarFeedDraft { Kind = "caldav", Server = "https://dav.example", Username = "me" }));
        Assert.IsFalse(CalendarFeedEditorModel.IsComplete(new CalendarFeedDraft { Kind = "caldav", Server = "https://dav.example", Username = "  " }), "a blank username is incomplete");
        Assert.IsFalse(CalendarFeedEditorModel.IsComplete(new CalendarFeedDraft { Kind = "caldav", Server = "ftp://dav.example", Username = "me" }), "a non-http(s) server is incomplete");
    }
}

[TestClass]
public class CalendarWidgetEditorProviderTests
{
    [TestMethod]
    public void GetEditorKind_FeedsJson_IsTheCalendarFeedsEditor()
    {
        var widget = new CalendarWidget();
        var provider = (IWidgetEditorProvider)widget;
        PropertyInfo feedsProp = typeof(CalendarWidget).GetProperty(nameof(CalendarWidget.FeedsJson))!;

        Assert.AreEqual(EditorKind.CalendarFeeds, provider.GetEditorKind(feedsProp));
    }

    [TestMethod]
    public void GetEditorKind_OtherProperties_UseTheGenericEditor()
    {
        var widget = new CalendarWidget();
        var provider = (IWidgetEditorProvider)widget;
        PropertyInfo pollProp = typeof(CalendarWidget).GetProperty(nameof(CalendarWidget.PollIntervalMinutes))!;

        Assert.IsNull(provider.GetEditorKind(pollProp));
    }
}
