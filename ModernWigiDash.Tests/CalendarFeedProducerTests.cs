using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the calendar feed producer's poll policy at its own seam: the conditional
/// fetch (ETag), the body-hash dedupe, the per-feed failure isolation (ADR-0017:
/// a dropout keeps its last-good events in the merged snapshot), and the CalDAV
/// credential-invalidation path. The producer writes to the static
/// <see cref="CalendarEventStore"/>, so each test resets the store, drives one
/// synchronous <c>PollTickAsync</c> through an in-memory <see cref="IFeedFetcher"/>
/// fake, and reads the merged snapshot back. This is the cluster's most
/// load-bearing behavior, previously unpinned.
/// </summary>
[TestClass]
public class CalendarFeedProducerTests
{
    private static readonly DateTime Now = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Unspecified);

    [TestCleanup]
    public void Cleanup() => CalendarEventStore.Reset();

    /// <summary>A scriptable in-memory fetcher: returns a canned result per call,
    /// or throws a scripted exception, recording the ETag it was handed.</summary>
    private sealed class FakeFetcher : IFeedFetcher
    {
        private readonly Queue<Func<string?, FeedFetchResult>> _script;
        public List<string?> EtagsSeen { get; } = [];

        public FakeFetcher(params Func<string?, FeedFetchResult>[] results)
            => _script = new(results);

        public Task<FeedFetchResult> FetchAsync(CalendarFeed feed, string credential, string? previousEtag, CancellationToken cancellationToken)
        {
            EtagsSeen.Add(previousEtag);
            if (_script.Count == 0)
                return Task.FromResult(new FeedFetchResult("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n", null));
            var next = _script.Dequeue();
            return Task.FromResult(next(previousEtag));
        }
    }

    /// <summary>A fetcher that routes to a per-feed script by the feed's id, so a
    /// single tick over multiple feeds can drive independent outcomes.</summary>
    private sealed class MultiFeedFetcher : IFeedFetcher
    {
        private readonly Queue<Func<string?, FeedFetchResult>> _good;
        private readonly Queue<Func<string?, FeedFetchResult>> _bad;
        private readonly List<string?> _etagsSeen;

        public MultiFeedFetcher(Queue<Func<string?, FeedFetchResult>> good, Queue<Func<string?, FeedFetchResult>> bad, List<string?> etagsSeen)
        {
            _good = good;
            _bad = bad;
            _etagsSeen = etagsSeen;
        }

        public Task<FeedFetchResult> FetchAsync(CalendarFeed feed, string credential, string? previousEtag, CancellationToken cancellationToken)
        {
            _etagsSeen.Add(previousEtag);
            var queue = feed.FeedId == "good" ? _good : _bad;
            var next = queue.Dequeue();
            return Task.FromResult(next(previousEtag));
        }
    }

    private static CalendarFeedProducer Make(FakeFetcher fetcher)
    {
        var logs = new List<string>();
        // The default credential store (its DPAPI round-trip is exercised by
        // CalendarCredentialStoreTests; here it just needs to exist).
        var creds = new CalendarCredentialStore();
        return new CalendarFeedProducer(() => fetcher, creds, () => Now, TimeSpan.FromMinutes(30), line => logs.Add(line));
    }

    private static IcsUrlFeed Ics(string id, string label = "Work")
        => new() { FeedId = id, Label = label, Url = $"https://cal.example/{id}" };

    private static string IcsBody(string summary, DateTime start, DateTime end)
        => $"""
             BEGIN:VCALENDAR
             VERSION:2.0
             BEGIN:VEVENT
             UID:{Guid.NewGuid():N}
             DTSTART:{start:yyyyMMddTHHmmss}
             DTEND:{end:yyyyMMddTHHmmss}
             SUMMARY:{summary}
             END:VEVENT
             END:VCALENDAR
             """;

    [TestMethod]
    public async Task PollTick_FreshBody_ParsesAndMergesEvents()
    {
        var fetcher = new FakeFetcher(_ => new FeedFetchResult(IcsBody("Standup", Now.AddHours(1), Now.AddHours(1).AddMinutes(30)), "etag-1"));
        var producer = Make(fetcher);

        await producer.PollTickAsync([Ics("a")]);

        var snap = CalendarEventStore.ReadSnapshot();
        Assert.IsTrue(snap.HasData);
        Assert.IsTrue(snap.IsLive);
        Assert.AreEqual(1, snap.Events.Count);
        Assert.AreEqual("Standup", snap.Events[0].Title);
    }

    [TestMethod]
    public async Task PollTick_NotModified_KeepsLastGoodEvents()
    {
        var fetcher = new FakeFetcher(
            _ => new FeedFetchResult(IcsBody("First", Now.AddHours(1), Now.AddHours(2)), "etag-1"),
            _ => new FeedFetchResult(null, "etag-1")); // 304 not-modified
        var producer = Make(fetcher);
        var feeds = new[] { Ics("a") };

        await producer.PollTickAsync(feeds);
        await producer.PollTickAsync(feeds);

        var snap = CalendarEventStore.ReadSnapshot();
        Assert.AreEqual(1, snap.Events.Count);
        Assert.AreEqual("First", snap.Events[0].Title);
        // The second tick sent the stored ETag (conditional fetch).
        Assert.AreEqual("etag-1", fetcher.EtagsSeen[1]);
    }

    [TestMethod]
    public async Task PollTick_IdenticalBody_SkipsReparseButKeepsEvents()
    {
        string body = IcsBody("Same", Now.AddHours(1), Now.AddHours(2));
        var fetcher = new FakeFetcher(
            _ => new FeedFetchResult(body, "e1"),
            _ => new FeedFetchResult(body, "e2")); // provider ignored If-None-Match, full body again
        var producer = Make(fetcher);
        var feeds = new[] { Ics("a") };

        await producer.PollTickAsync(feeds);
        await producer.PollTickAsync(feeds);

        var snap = CalendarEventStore.ReadSnapshot();
        // The dedupe reuses the parsed events; the snapshot still carries them.
        Assert.AreEqual(1, snap.Events.Count);
        Assert.AreEqual("Same", snap.Events[0].Title);
    }

    [TestMethod]
    public async Task PollTick_OneFeedFails_OtherFeedsStillMerge_AndFailedFeedKeepsLastGood()
    {
        // Feed "good" always succeeds; feed "bad" fails on the second tick only.
        // One fetcher that branches on the feed's own id (the producer hands out
        // a fresh fetcher per feed within a tick; the fake routes by FeedId).
        var logs = new List<string>();
        var creds = new CalendarCredentialStore();
        var goodScript = new Queue<Func<string?, FeedFetchResult>>(
        [
            _ => new FeedFetchResult(IcsBody("Good", Now.AddHours(1), Now.AddHours(2)), "g1"),
            _ => new FeedFetchResult(IcsBody("Good", Now.AddHours(1), Now.AddHours(2)), "g1"),
        ]);
        var badScript = new Queue<Func<string?, FeedFetchResult>>(
        [
            _ => new FeedFetchResult(IcsBody("Bad", Now.AddHours(3), Now.AddHours(4)), "b1"),
            _ => throw new InvalidOperationException("network down"),
        ]);
        var etagsSeen = new List<string?>();
        var multiFetcher = new MultiFeedFetcher(goodScript, badScript, etagsSeen);
        var producer = new CalendarFeedProducer(
            () => multiFetcher, creds, () => Now, TimeSpan.FromMinutes(30), line => logs.Add(line));

        var feeds = new[] { Ics("good"), Ics("bad") };
        await producer.PollTickAsync(feeds);
        int afterFirst = CalendarEventStore.ReadSnapshot().Events.Count;
        Assert.AreEqual(2, afterFirst, "both feeds merge on the first tick");

        // Second tick: "bad" throws; "good" still updates, and "bad" keeps its last-good event.
        await producer.PollTickAsync(feeds);

        var snap = CalendarEventStore.ReadSnapshot();
        // The failed feed's last-good event survives (ADR-0017): no hole.
        Assert.IsTrue(snap.Events.Any(e => e.Title == "Bad"), "the dropped feed keeps its last-known event");
        Assert.IsTrue(snap.Events.Any(e => e.Title == "Good"), "the healthy feed still updated");
    }

    [TestMethod]
    public async Task PollTick_CalDavAuthFailure_InvalidatesEtagSoNextPollResolves()
    {
        var fetcher = new FakeFetcher(
            prev => throw new CalDavAuthException("401"),
            prev => new FeedFetchResult(IcsBody("Recovered", Now.AddHours(1), Now.AddHours(2)), "new-etag"));
        var producer = Make(fetcher);
        var feed = new CalDavFeed { FeedId = "icloud", Label = "iCloud", Server = "https://caldav.icloud.com", PrincipalPath = "/cal/", Username = "me" };
        var feeds = new[] { feed };

        await producer.PollTickAsync(feeds);
        // After the auth failure, the stored ETag is nulled: the next fetch is handed a null ETag.
        Assert.IsNull(fetcher.EtagsSeen[^1]);
        await producer.PollTickAsync(feeds);
        Assert.AreEqual(1, CalendarEventStore.ReadSnapshot().Events.Count);
    }

    [TestMethod]
    public async Task PollTick_DisabledFeed_IsSkipped()
    {
        var fetcher = new FakeFetcher(_ => new FeedFetchResult(IcsBody("X", Now.AddHours(1), Now.AddHours(2)), "e"));
        var producer = Make(fetcher);
        var feed = new IcsUrlFeed { FeedId = "a", Label = "Work", Url = "https://cal.example/a", Enabled = false };

        await producer.PollTickAsync([feed]);

        Assert.AreEqual(0, fetcher.EtagsSeen.Count, "a disabled feed makes no fetch");
        Assert.IsFalse(CalendarEventStore.ReadSnapshot().HasData);
    }
}
