using System.Net;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;

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

/// <summary>
/// Pins the CalDAV PROPFIND request bodies as well-formed XML. The two
/// templates are <see cref="XDocument"/> fields initialized at
/// <see cref="CalDavFetcher"/> type-init; a malformed body throws a
/// <see cref="TypeInitializationException"/> the first time the fetcher type is
/// touched, which in production surfaced as the inspector's feed-list commit
/// aborting before persistence (the producer restart references the fetcher).
/// Parsing each template here makes the malformation fail a test instead of the
/// on-device loop.
/// </summary>
[TestClass]
public class CalDavPropFindTemplateTests
{
    [TestMethod]
    public void HomeSetPropFind_IsWellFormedXml()
    {
        // Accessing the field runs the static initializer; a malformed body
        // throws here (and would throw at first use in production).
        XDocument doc = CalDavFetcher.HomeSetPropFind;

        Assert.IsTrue(doc.Root is { } root && root.Name.LocalName == "propfind");
    }

    [TestMethod]
    public void CalendarListPropFind_IsWellFormedXml()
    {
        XDocument doc = CalDavFetcher.CalendarListPropFind;

        Assert.IsTrue(doc.Root is { } root && root.Name.LocalName == "propfind");
    }
}

/// <summary>
/// CalDAV fetcher tests: pins HTTP Basic authentication header formatting
/// (RFC 7617 username:password base64) and PROPFIND XML methods.
/// </summary>
[TestClass]
public class CalDavFetcherTests
{
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

    [TestMethod]
    public async Task DiscoverAsync_SendsBasicAuthWithUsernameAndPassword_AndPropFindMethod()
    {
        string propFindResponse = """
            <?xml version="1.0" encoding="utf-8"?>
            <multistatus xmlns="DAV:">
                <response>
                    <href>/calendars/user/home/</href>
                    <propstat>
                        <prop>
                            <calendar-home-set xmlns="urn:ietf:params:xml:ns:caldav">
                                <href>/calendars/user/home/</href>
                            </calendar-home-set>
                        </prop>
                        <status>HTTP/1.1 200 OK</status>
                    </propstat>
                </response>
            </multistatus>
            """;

        string calendarListResponse = """
            <?xml version="1.0" encoding="utf-8"?>
            <multistatus xmlns="DAV:">
                <response>
                    <href>/calendars/user/home/work/</href>
                    <propstat>
                        <prop>
                            <displayname>Work</displayname>
                        </prop>
                        <status>HTTP/1.1 200 OK</status>
                    </propstat>
                </response>
            </multistatus>
            """;

        int callIndex = 0;
        var handler = new RecordingHandler(_ =>
        {
            string content = callIndex++ == 0 ? propFindResponse : calendarListResponse;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/xml")
            };
        });

        using var client = new HttpClient(handler);
        var fetcher = new CalDavFetcher(() => client);

        var feed = new CalDavFeed
        {
            FeedId = "f1",
            Label = "Personal",
            ColorHex = "#ff0000",
            Server = "caldav.example.com",
            Port = 443,
            PrincipalPath = "/calendars/john_doe/",
            Username = "john_doe"
        };

        var calendars = await fetcher.DiscoverAsync(feed, "secret_pass", CancellationToken.None);

        Assert.AreEqual(1, calendars.Count);
        Assert.AreEqual("Work", calendars[0].DisplayName);
        Assert.AreEqual(2, handler.Requests.Count);

        foreach (var req in handler.Requests)
        {
            Assert.AreEqual("PROPFIND", req.Method.Method);
            Assert.IsNotNull(req.Headers.Authorization);
            Assert.AreEqual("Basic", req.Headers.Authorization.Scheme);
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(req.Headers.Authorization.Parameter!));
            Assert.AreEqual("john_doe:secret_pass", decoded, "Basic auth header must contain username:password per RFC 7617");
        }
    }

    [TestMethod]
    public async Task FetchAsync_WithSelectedCalendar_SendsBasicAuthWithUsernameAndPassword()
    {
        string icsContent = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Example Corp.//EN
            BEGIN:VEVENT
            UID:event1@example.com
            DTSTAMP:20260907T120000Z
            DTSTART:20260907T140000Z
            DTEND:20260907T150000Z
            SUMMARY:Team Meeting
            END:VEVENT
            END:VCALENDAR
            """;

        var handler = new RecordingHandler(_ =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(icsContent, Encoding.UTF8, "text/calendar")
            };
        });

        using var client = new HttpClient(handler);
        var fetcher = new CalDavFetcher(() => client);

        var feed = new CalDavFeed
        {
            FeedId = "f2",
            Label = "Team",
            ColorHex = "#00ff00",
            Server = "caldav.example.com",
            Port = 443,
            PrincipalPath = "/calendars/alice/",
            Username = "alice",
            SelectedCalendars = ["https://caldav.example.com/calendars/alice/team.ics"]
        };

        var result = await fetcher.FetchAsync(feed, "alice_secret", null, CancellationToken.None);

        Assert.IsNotNull(result.Body);
        Assert.IsTrue(result.Body.Contains("Team Meeting", StringComparison.Ordinal));
        Assert.AreEqual(1, handler.Requests.Count);

        var req = handler.Requests[0];
        Assert.AreEqual(HttpMethod.Get, req.Method);
        Assert.IsNotNull(req.Headers.Authorization);
        Assert.AreEqual("Basic", req.Headers.Authorization.Scheme);
        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(req.Headers.Authorization.Parameter!));
        Assert.AreEqual("alice:alice_secret", decoded, "Basic auth header must contain username:password");
    }

    [TestMethod]
    public async Task DiscoverAsync_OverHttp_WithCredential_RefusesToSendCredentials()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<multistatus xmlns=\"DAV:\"></multistatus>", Encoding.UTF8, "text/xml")
            });

        using var client = new HttpClient(handler);
        var fetcher = new CalDavFetcher(() => client);

        var feed = new CalDavFeed
        {
            FeedId = "f-http",
            Label = "HTTP test",
            ColorHex = "#ff0000",
            Server = "caldav.example.com",
            Port = 80,
            PrincipalPath = "/calendars/john/",
            Username = "john"
        };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => fetcher.DiscoverAsync(feed, "secret", CancellationToken.None),
            "CalDAV credentials must not be sent over cleartext HTTP.");
    }

    [TestMethod]
    public async Task FetchAsync_HrefResolvingToDifferentOrigin_RefusesToFetch()
    {
        // A malicious server returns an href pointing at a different host.
        string maliciousPropFind = """
            <?xml version="1.0" encoding="utf-8"?>
            <multistatus xmlns="DAV:">
                <response>
                    <href>http://evil.example.com/steal</href>
                    <propstat>
                        <prop>
                            <displayname>Evil</displayname>
                        </prop>
                        <status>HTTP/1.1 200 OK</status>
                    </propstat>
                </response>
            </multistatus>
            """;

        int callIndex = 0;
        var handler = new RecordingHandler(_ =>
        {
            string content = callIndex++ == 0 ? HomeSetResponse : maliciousPropFind;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/xml")
            };
        });

        using var client = new HttpClient(handler);
        var fetcher = new CalDavFetcher(() => client);

        var feed = new CalDavFeed
        {
            FeedId = "f-ssrf",
            Label = "SSRF test",
            ColorHex = "#ff0000",
            Server = "caldav.example.com",
            Port = 443,
            PrincipalPath = "/calendars/john/",
            Username = "john"
        };

        await Assert.ThrowsAsync<HttpRequestException>(
            () => fetcher.DiscoverAsync(feed, "secret", CancellationToken.None),
            "A server-supplied href resolving to a different origin must be refused.");
    }

    private const string HomeSetResponse = """
        <?xml version="1.0" encoding="utf-8"?>
        <multistatus xmlns="DAV:">
            <response>
                <href>/calendars/user/home/</href>
                <propstat>
                    <prop>
                        <calendar-home-set xmlns="urn:ietf:params:xml:ns:caldav">
                            <href>/calendars/user/home/</href>
                        </calendar-home-set>
                    </prop>
                    <status>HTTP/1.1 200 OK</status>
                </propstat>
            </response>
        </multistatus>
        """;
}
