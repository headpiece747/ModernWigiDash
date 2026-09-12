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
        string original = """[{"kind":"ics","feedId":"g","label":"Google","color":"#4F8CFF","url":"https://cal.example/ics","enabled":true},{"kind":"caldav","feedId":"icloud","label":"iCloud","color":"#22C55E","server":"https://caldav.icloud.com","port":443,"principalPath":"/p/","username":"me","selectedCalendars":["/calendars/me/work/"],"enabled":true}]""";

        string serialized = CalendarFeedEditorModel.Serialize(CalendarFeedEditorModel.Parse(original));
        IReadOnlyList<CalendarFeedDraft> reparsed = CalendarFeedEditorModel.Parse(serialized);
        IReadOnlyList<CalendarFeedDraft> originalParsed = CalendarFeedEditorModel.Parse(original);

        Assert.AreEqual(originalParsed.Count, reparsed.Count, "parse -> serialize -> parse keeps the same feed count");
        for (int i = 0; i < originalParsed.Count; i++)
        {
            Assert.AreEqual(originalParsed[i].Kind, reparsed[i].Kind, $"feed {i} kind");
            Assert.AreEqual(originalParsed[i].FeedId, reparsed[i].FeedId, $"feed {i} id");
            Assert.AreEqual(originalParsed[i].Label, reparsed[i].Label, $"feed {i} label");
            Assert.AreEqual(originalParsed[i].ColorHex, reparsed[i].ColorHex, $"feed {i} color survives the round-trip");
            Assert.AreEqual(originalParsed[i].Url, reparsed[i].Url, $"feed {i} url");
            Assert.AreEqual(originalParsed[i].Server, reparsed[i].Server, $"feed {i} server");
            Assert.AreEqual(originalParsed[i].Port, reparsed[i].Port, $"feed {i} port");
            Assert.AreEqual(originalParsed[i].PrincipalPath, reparsed[i].PrincipalPath, $"feed {i} principal");
            Assert.AreEqual(originalParsed[i].Username, reparsed[i].Username, $"feed {i} username");
            CollectionAssert.AreEqual(new List<string>(originalParsed[i].SelectedCalendars), new List<string>(reparsed[i].SelectedCalendars), $"feed {i} selected calendars survive the round-trip");
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

/// <summary>
/// Pins the feed editor's row-state module at its own seam: the draft-list
/// transitions (add, remove, edit a field, swap kind) and the two commit routes
/// (the whole list through the write-back funnel, the CalDAV password through the
/// credential seam). These tests drive the module directly with in-memory
/// callbacks, so the bookkeeping is verified where it is owned rather than only
/// through a WPF tree.
/// </summary>
[TestClass]
public class CalendarFeedEditorRowsTests
{
    private static (CalendarFeedEditorRows Rows, List<string> Commits, List<(string FeedId, string Password)> Credentials) Make(
        string? feedsJson = null,
        bool withCredentialSeam = true)
    {
        var commits = new List<string>();
        var credentials = new List<(string, string)>();
        var rows = new CalendarFeedEditorRows(
            feedsJson,
            commit => commits.Add(commit),
            withCredentialSeam ? (Action<string, string>)((id, pw) => credentials.Add((id, pw))) : null);
        return (rows, commits, credentials);
    }

    [TestMethod]
    public void Add_AppendsABlankDraftAndCommitsTheNewList()
    {
        var (rows, commits, _) = Make();

        rows.Add();

        Assert.AreEqual(1, rows.Drafts.Count);
        Assert.AreEqual("ics", rows.Drafts[0].Kind);
        // The commit carries the serialized list (one complete ics feed after the blank is dropped on serialize).
        Assert.AreEqual(1, commits.Count);
    }

    [TestMethod]
    public void Remove_DropsTheRowAndCommits()
    {
        string json = """[{"kind":"ics","feedId":"a","label":"A","url":"https://x.example/a"},{"kind":"ics","feedId":"b","label":"B","url":"https://x.example/b"}]""";
        var (rows, commits, _) = Make(json);
        int before = rows.Drafts.Count;

        rows.Remove(0);

        Assert.AreEqual(before - 1, rows.Drafts.Count);
        Assert.AreEqual("b", rows.Drafts[0].FeedId);
        Assert.IsTrue(commits.Count > 0);
    }

    [TestMethod]
    public void SetLabel_WritesTheFieldAndCommits()
    {
        string json = """[{"kind":"ics","feedId":"a","label":"A","url":"https://x.example/a"}]""";
        var (rows, commits, _) = Make(json);
        int commitsBefore = commits.Count;

        rows.SetLabel(0, "Renamed");

        Assert.AreEqual("Renamed", rows.Drafts[0].Label);
        Assert.AreEqual(commitsBefore + 1, commits.Count);
    }

    [TestMethod]
    public void SetKind_SwapsTheRowKindAndCommits()
    {
        string json = """[{"kind":"ics","feedId":"a","label":"A","url":"https://x.example/a"}]""";
        var (rows, commits, _) = Make(json);

        rows.SetKind(0, "caldav");

        Assert.AreEqual("caldav", rows.Drafts[0].Kind);
        Assert.IsTrue(commits.Count > 0);
    }

    [TestMethod]
    public void SavePassword_RoutesThroughTheCredentialSeamNotTheCommit()
    {
        string json = """[{"kind":"caldav","feedId":"icloud","label":"iCloud","server":"https://caldav.icloud.com","port":443,"principalPath":"/cal/","username":"me"}]""";
        var (rows, commits, credentials) = Make(json);
        int commitsBefore = commits.Count;

        rows.SavePassword(0, "secret");

        // The password rides the credential seam, never the persisted JSON commit.
        Assert.AreEqual(1, credentials.Count);
        Assert.AreEqual(("icloud", "secret"), credentials[0]);
        Assert.AreEqual(commitsBefore, commits.Count, "a password save must not trigger a FeedsJson commit");
    }

    [TestMethod]
    public void SavePassword_WithoutACredentialSeam_IsANoOp()
    {
        string json = """[{"kind":"caldav","feedId":"icloud","label":"iCloud","server":"https://caldav.icloud.com","port":443,"principalPath":"/cal/","username":"me"}]""";
        var (rows, _, credentials) = Make(json, withCredentialSeam: false);

        rows.SavePassword(0, "secret");

        Assert.AreEqual(0, credentials.Count);
    }

    [TestMethod]
    public void SetUrl_WritesTheFieldAndCommits()
    {
        string json = """[{"kind":"ics","feedId":"a","label":"A","url":"https://x.example/a"}]""";
        var (rows, commits, _) = Make(json);
        int commitsBefore = commits.Count;

        rows.SetUrl(0, "https://y.example/b");

        Assert.AreEqual("https://y.example/b", rows.Drafts[0].Url);
        Assert.AreEqual(commitsBefore + 1, commits.Count);
    }

    [TestMethod]
    public void SetServer_SetPrincipalPath_SetUsername_WriteTheFieldsAndCommit()
    {
        string json = """[{"kind":"caldav","feedId":"icloud","label":"iCloud","server":"https://caldav.icloud.com","port":443,"principalPath":"/cal/","username":"me"}]""";
        var (rows, commits, _) = Make(json);
        int commitsBefore = commits.Count;

        rows.SetServer(0, "https://new.example");
        rows.SetPrincipalPath(0, "/CalDAV/");
        rows.SetUsername(0, "you");

        Assert.AreEqual("https://new.example", rows.Drafts[0].Server);
        Assert.AreEqual("/CalDAV/", rows.Drafts[0].PrincipalPath);
        Assert.AreEqual("you", rows.Drafts[0].Username);
        Assert.AreEqual(commitsBefore + 3, commits.Count);
    }

    [TestMethod]
    public void SetEnabled_TogglesTheRowFlagAndCommits()
    {
        string json = """[{"kind":"ics","feedId":"a","label":"A","url":"https://x.example/a","enabled":true}]""";
        var (rows, commits, _) = Make(json);
        Assert.IsTrue(rows.Drafts[0].Enabled);
        int commitsBefore = commits.Count;

        rows.SetEnabled(0, false);

        Assert.IsFalse(rows.Drafts[0].Enabled);
        Assert.AreEqual(commitsBefore + 1, commits.Count);
    }

    [TestMethod]
    public void OutOfRangeTransitions_AreNoOps()
    {
        var (rows, commits, _) = Make();
        int commitsBefore = commits.Count;

        rows.Remove(99);
        rows.SetLabel(99, "nope");
        rows.SetKind(99, "caldav");
        rows.SetUrl(99, "u");
        rows.SetServer(99, "s");
        rows.SetPrincipalPath(99, "p");
        rows.SetUsername(99, "n");
        rows.SetEnabled(99, true);
        rows.SavePassword(99, "pw");

        Assert.AreEqual(0, rows.Drafts.Count);
        Assert.AreEqual(commitsBefore, commits.Count, "an out-of-range transition must not commit");
    }
}
