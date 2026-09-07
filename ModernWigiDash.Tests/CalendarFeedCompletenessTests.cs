namespace ModernWigiDash.Tests;

/// <summary>
/// The calendar feed's completeness rule pinned at its module interface (no
/// editor, no WPF): an ics feed needs a non-empty http(s) subscribe URL; a
/// CalDAV feed needs a non-empty http(s) server and a username. This is the one
/// owner of "what makes a feed valid", sitting beside the field definitions it
/// validates, so the inspector's editor and any future producer-side guard
/// route through the same rule instead of re-deriving it.
/// </summary>
[TestClass]
public class CalendarFeedCompletenessTests
{
    [TestMethod]
    public void Ics_RequiresAnAbsoluteHttpUrl()
    {
        Assert.IsTrue(CalendarFeedCompleteness.IsComplete(new IcsUrlFeed { FeedId = "g", Label = "G", Url = "https://x.example/ics" }));
        Assert.IsTrue(CalendarFeedCompleteness.IsComplete(new IcsUrlFeed { FeedId = "g", Label = "G", Url = "http://x.example/ics" }));
        Assert.IsFalse(CalendarFeedCompleteness.IsComplete(new IcsUrlFeed { FeedId = "g", Label = "G", Url = "file:///etc/passwd" }), "a non-http(s) URL is incomplete");
        Assert.IsFalse(CalendarFeedCompleteness.IsComplete(new IcsUrlFeed { FeedId = "g", Label = "G", Url = "" }), "a blank URL is incomplete");
        Assert.IsFalse(CalendarFeedCompleteness.IsComplete(new IcsUrlFeed { FeedId = "g", Label = "G", Url = "not a url" }), "a non-absolute value is incomplete");
    }

    [TestMethod]
    public void CalDav_RequiresAnHttpServerAndAUsername()
    {
        Assert.IsTrue(CalendarFeedCompleteness.IsComplete(new CalDavFeed { FeedId = "d", Label = "D", Server = "https://dav.example", PrincipalPath = "/p/", Username = "me" }));
        Assert.IsFalse(CalendarFeedCompleteness.IsComplete(new CalDavFeed { FeedId = "d", Label = "D", Server = "https://dav.example", PrincipalPath = "/p/", Username = "  " }), "a blank username is incomplete");
        Assert.IsFalse(CalendarFeedCompleteness.IsComplete(new CalDavFeed { FeedId = "d", Label = "D", Server = "ftp://dav.example", PrincipalPath = "/p/", Username = "me" }), "a non-http(s) server is incomplete");
        Assert.IsFalse(CalendarFeedCompleteness.IsComplete(new CalDavFeed { FeedId = "d", Label = "D", Server = "", PrincipalPath = "/p/", Username = "me" }), "a blank server is incomplete");
    }

    [TestMethod]
    public void KindConstant_MatchesTheCalDavArm()
    {
        // The kind discriminator has one spelling: the constant the codec reads
        // and writes agrees with the CalDavFeed arm's own Kind property, so the
        // validity decision and the shape decision cannot drift on what names
        // the CalDAV arm.
        Assert.AreEqual(CalendarFeedCompleteness.CalDavKind, new CalDavFeed { FeedId = "d", Label = "D", Server = "s", PrincipalPath = "p", Username = "u" }.Kind);
    }
}
