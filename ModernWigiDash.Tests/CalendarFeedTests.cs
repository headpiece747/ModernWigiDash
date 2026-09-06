namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the calendar feed's identity and policy rules without a network, a
/// fetcher, or pixels. The identity (<see cref="CalendarFeed.Build"/> /
/// <see cref="CalendarFeed.SameKey"/>) is the one spelling of "which feed is
/// this" the store's dedupe and the producer's conditional-fetch stamp route
/// through; the policy (<see cref="CalendarFeedPolicy"/>) owns the tuning
/// constants and the hand-edited-value resolution. Both are assertable at the
/// module interface.
/// </summary>
[TestClass]
public class CalendarFeedTests
{
    // --- IcsUrlFeed identity -------------------------------------------------

    [TestMethod]
    public void IcsUrl_Build_CarriesTheNonSecretConfig()
    {
        IcsUrlFeed feed = new()
        {
            FeedId = "work",
            Label = "Work",
            ColorHex = "#FF0000",
            Url = "https://calendar.google.com/calendar/ics/abc"
        };

        string key = feed.Build();

        Assert.IsTrue(key.StartsWith("ics|", StringComparison.Ordinal),
            "the kind discriminator leads the key");
        Assert.IsTrue(key.Contains("work", StringComparison.Ordinal));
        Assert.IsTrue(key.Contains("https://calendar.google.com/calendar/ics/abc", StringComparison.Ordinal));
    }

    [TestMethod]
    public void IcsUrl_UrlChange_IsAnIdentityChange()
    {
        IcsUrlFeed a = MakeIcs(url: "https://example.com/a.ics");
        IcsUrlFeed b = MakeIcs(url: "https://example.com/b.ics");

        Assert.IsFalse(CalendarFeed.SameKey(a.Build(), b.Build()),
            "editing the URL names a different feed and forces a fresh fetch");
    }

    [TestMethod]
    public void IcsUrl_SameConfig_SameKey()
    {
        IcsUrlFeed a = MakeIcs();
        IcsUrlFeed b = MakeIcs();

        Assert.IsTrue(CalendarFeed.SameKey(a.Build(), b.Build()));
    }

    [TestMethod]
    public void IcsUrl_FeedIdChange_IsAnIdentityChange()
    {
        IcsUrlFeed a = MakeIcs(feedId: "one");
        IcsUrlFeed b = MakeIcs(feedId: "two");

        Assert.IsFalse(CalendarFeed.SameKey(a.Build(), b.Build()),
            "two feeds with different ids are different feeds even if every other field matches");
    }

    [TestMethod]
    public void IcsUrl_SeparatorInField_CannotForgeACollidingKey()
    {
        // A '|' inside the URL must be escaped so it cannot split into a fake
        // extra field and collide with a differently-shaped feed.
        IcsUrlFeed withPipe = MakeIcs(url: "https://example.com/a|b.ics");
        IcsUrlFeed twoFields = new()
        {
            FeedId = "f",
            Label = "L",
            ColorHex = "",
            Url = "https://example.com/a"
        };

        Assert.IsFalse(CalendarFeed.SameKey(withPipe.Build(), twoFields.Build()),
            "an unescaped separator would let a pipe in one field forge a colliding key");
    }

    // --- CalDavFeed identity -------------------------------------------------

    [TestMethod]
    public void CalDav_Build_CarriesServerPrincipalUsernameButNotPassword()
    {
        CalDavFeed feed = new()
        {
            FeedId = "icloud",
            Label = "iCloud",
            Server = "caldav.icloud.com",
            Port = 443,
            PrincipalPath = "/calendars/alice@example.com/",
            Username = "alice@example.com"
        };

        string key = feed.Build();

        Assert.IsTrue(key.StartsWith("caldav|", StringComparison.Ordinal));
        Assert.IsTrue(key.Contains("caldav.icloud.com", StringComparison.Ordinal));
        Assert.IsTrue(key.Contains("/calendars/alice@example.com/", StringComparison.Ordinal));
        Assert.IsTrue(key.Contains("alice@example.com", StringComparison.Ordinal));
        // The password is never a field of the union, so it cannot ride the key.
        Assert.IsFalse(key.Contains("password", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CalDav_SelectedCalendars_RideTheKeyInOrder()
    {
        CalDavFeed a = MakeCalDav(selected: ["home", "work"]);
        CalDavFeed b = MakeCalDav(selected: ["work", "home"]);

        Assert.IsFalse(CalendarFeed.SameKey(a.Build(), b.Build()),
            "the selected-calendar set is part of the identity; order matters for the stamp");
    }

    [TestMethod]
    public void CalDav_ServerChange_IsAnIdentityChange()
    {
        CalDavFeed a = MakeCalDav(server: "caldav.icloud.com");
        CalDavFeed b = MakeCalDav(server: "caldav.fastmail.com");

        Assert.IsFalse(CalendarFeed.SameKey(a.Build(), b.Build()));
    }

    [TestMethod]
    public void CalDav_PortChange_IsAnIdentityChange()
    {
        CalDavFeed a = MakeCalDav(port: 443);
        CalDavFeed b = MakeCalDav(port: 8080);

        Assert.IsFalse(CalendarFeed.SameKey(a.Build(), b.Build()));
    }

    // --- Kind discriminators -------------------------------------------------

    [TestMethod]
    public void Kind_IcsAndCalDav_AreDistinctSpellings()
    {
        Assert.AreEqual("ics", MakeIcs().Kind);
        Assert.AreEqual("caldav", MakeCalDav().Kind);
    }

    // --- SameKey predicate ---------------------------------------------------

    [TestMethod]
    public void SameKey_IsOrdinalCaseSensitiveAndNullSafe()
    {
        Assert.IsTrue(CalendarFeed.SameKey(null, null));
        Assert.IsFalse(CalendarFeed.SameKey(null, "x"));
        Assert.IsFalse(CalendarFeed.SameKey("X", "x"), "case is identity: a case change is a new feed");
        Assert.IsTrue(CalendarFeed.SameKey("x", "x"));
    }

    // --- Policy: poll interval ----------------------------------------------

    [TestMethod]
    public void ResolvePollInterval_KnownChoice_PassesThrough()
    {
        foreach (int m in CalendarFeedPolicy.PollIntervalMinutes)
            Assert.AreEqual(m, CalendarFeedPolicy.ResolvePollInterval(m));
    }

    [TestMethod]
    public void ResolvePollInterval_AbsentOrUnknown_DegradesToDefault()
    {
        Assert.AreEqual(CalendarFeedPolicy.DefaultPollIntervalMinutes, CalendarFeedPolicy.ResolvePollInterval(null));
        Assert.AreEqual(CalendarFeedPolicy.DefaultPollIntervalMinutes, CalendarFeedPolicy.ResolvePollInterval(5),
            "a hand-edited out-of-range value degrades to the default, never an arbitrary cadence");
        Assert.AreEqual(CalendarFeedPolicy.DefaultPollIntervalMinutes, CalendarFeedPolicy.ResolvePollInterval(0));
        Assert.AreEqual(CalendarFeedPolicy.DefaultPollIntervalMinutes, CalendarFeedPolicy.ResolvePollInterval(-1));
    }

    [TestMethod]
    public void PollInterval_DefaultIsAValidChoice()
    {
        Assert.IsTrue(Array.IndexOf(CalendarFeedPolicy.PollIntervalMinutes,
                CalendarFeedPolicy.DefaultPollIntervalMinutes) >= 0,
            "the default must be one of the offered choices");
    }

    // --- Policy: timed rows --------------------------------------------------

    [TestMethod]
    public void ResolveTimedRows_InRange_PassesThrough()
    {
        for (int r = CalendarFeedPolicy.MinTimedRows; r <= CalendarFeedPolicy.MaxTimedRows; r++)
            Assert.AreEqual(r, CalendarFeedPolicy.ResolveTimedRows(r));
    }

    [TestMethod]
    public void ResolveTimedRows_OutOfRangeOrAbsent_DegradesToDefault()
    {
        Assert.AreEqual(CalendarFeedPolicy.DefaultTimedRows, CalendarFeedPolicy.ResolveTimedRows(null));
        Assert.AreEqual(CalendarFeedPolicy.DefaultTimedRows, CalendarFeedPolicy.ResolveTimedRows(0),
            "below the minimum degrades to the default");
        Assert.AreEqual(CalendarFeedPolicy.DefaultTimedRows, CalendarFeedPolicy.ResolveTimedRows(99),
            "above the maximum degrades to the default: a hand-edited profile can never smuggle in a row count the layout cannot draw");
    }

    [TestMethod]
    public void TimedRows_DefaultIsInRange()
    {
        int d = CalendarFeedPolicy.DefaultTimedRows;
        Assert.IsTrue(d >= CalendarFeedPolicy.MinTimedRows && d <= CalendarFeedPolicy.MaxTimedRows);
    }

    // --- Policy: window + size bound ----------------------------------------
    // These pins assert the tuning constants against their intended values; the
    // constants ARE the pinned contract, so the "always true" form is deliberate
    // (a future edit to a constant fails here). The analyzer's always-true flag
    // is suppressed for exactly these assertions.

    [TestMethod]
    public void Window_BoundsAreSane()
    {
#pragma warning disable MSTEST0032 // Regression guard: pin the window bounds to their intended values
        Assert.AreEqual(1, CalendarFeedPolicy.WindowBackDays,
            "one day back: a long-running event started yesterday still shows as active today");
        Assert.AreEqual(14, CalendarFeedPolicy.WindowForwardDays,
            "the default agenda horizon, bounded so open recurrences terminate");
#pragma warning restore MSTEST0032
    }

    [TestMethod]
    public void SizeBound_IsTwoMebibytes()
    {
#pragma warning disable MSTEST0032 // Regression guard: pin the raw-feed size bound
        Assert.AreEqual(2 * 1024 * 1024, CalendarFeedPolicy.MaxFeedBytes);
#pragma warning restore MSTEST0032
    }

    // --- helpers -------------------------------------------------------------

    private static IcsUrlFeed MakeIcs(string feedId = "work", string url = "https://example.com/work.ics")
        => new()
        {
            FeedId = feedId,
            Label = "Work",
            ColorHex = "#FF0000",
            Url = url
        };

    private static CalDavFeed MakeCalDav(
        string server = "caldav.icloud.com",
        int port = 443,
        IReadOnlyList<string>? selected = null)
        => new()
        {
            FeedId = "icloud",
            Label = "iCloud",
            Server = server,
            Port = port,
            PrincipalPath = "/calendars/alice@example.com/",
            Username = "alice@example.com",
            SelectedCalendars = selected ?? []
        };
}
