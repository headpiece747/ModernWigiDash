using System.Security.Cryptography;
using System.Text;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The calendar feed producer: owns the poll loop that fetches every enabled
/// feed, merges their normalized events into one snapshot, and writes it to
/// <see cref="CalendarEventStore"/>. Per feed it applies the conditional-fetch
/// rule (send the last ETag; a not-modified verdict keeps the cached body) and
/// the body-hash dedupe (a provider that ignores validators still costs no
/// re-parse when the body is unchanged). A feed's transport failure is isolated:
/// the other feeds still update, and the failed feed keeps its last-good events
/// in the merged snapshot (ADR-0017 -- a dropout renders the last known agenda,
/// not a hole). Credentials are resolved from the machine-local store at fetch
/// time (never carried on the feed identity), and a CalDAV 401/403 invalidates
/// that feed's cached ETag so the next poll re-resolves the password.
/// </summary>
internal sealed class CalendarFeedProducer : IDisposable
{
    private readonly Func<IFeedFetcher> _fetcherFactory;
    private readonly CalendarCredentialStore _credentials;
    private readonly Func<DateTime> _now;
    private readonly Action<string> _log;
    private readonly TimeSpan _interval;
    private readonly LoopLifetime _lifetime = new(TimeSpan.FromSeconds(5));

    // Per-feed state: the last ETag (for conditional fetch) and the last body
    // hash (for the dedupe). Keyed by the feed's identity key.
    private readonly Dictionary<string, FeedState> _feedState = new();
    private readonly Lock _stateGate = new();

    private sealed class FeedState
    {
        public string? Etag;
        public string? BodyHash;
        public IReadOnlyList<CalendarEvent> LastGoodEvents = [];
    }

    /// <param name="fetcherFactory">Builds the fetcher for a feed kind (tests
    /// bind an in-memory fake; production binds IcsUrlFetcher / CalDavFetcher
    /// over the shared HttpClient).</param>
    /// <param name="credentials">The machine-local credential store (CalDAV
    /// passwords).</param>
    /// <param name="now">The clock seam (test-injectable; production is
    /// TimeProvider.System).</param>
    /// <param name="interval">The poll cadence (the widget's choice, resolved
    /// through CalendarFeedPolicy).</param>
    /// <param name="log">Log sink.</param>
    public CalendarFeedProducer(
        Func<IFeedFetcher> fetcherFactory,
        CalendarCredentialStore credentials,
        Func<DateTime> now,
        TimeSpan interval,
        Action<string> log)
    {
        _fetcherFactory = fetcherFactory;
        _credentials = credentials;
        _now = now;
        _log = log;
        _interval = interval;
    }

    /// <summary>The test seam: drives one poll synchronously (no background
    /// thread) so the fetch/dedupe/merge policy is assertable directly.</summary>
    internal async Task PollTickAsync(IReadOnlyList<CalendarFeed> feeds, CancellationToken ct = default)
    {
        var merged = new List<CalendarEvent>();
        bool anyData = false;
        bool anyLive = false;

        foreach (CalendarFeed feed in feeds.Where(f => f.Enabled))
        {
            if (ct.IsCancellationRequested) break;

            string key = feed.Build();
            FeedState state;
            lock (_stateGate)
            {
                if (_feedState.TryGetValue(key, out FeedState? existing))
                {
                    state = existing;
                }
                else
                {
                    state = new FeedState();
                    _feedState[key] = state;
                }
            }

            try
            {
                string credential = ResolveCredential(feed);
                IFeedFetcher fetcher = _fetcherFactory();
                FeedFetchResult result = await fetcher.FetchAsync(feed, credential, state.Etag, ct).ConfigureAwait(false);

                // A not-modified verdict (null body): keep the last-good events,
                // refresh nothing else. This is the common steady-state path.
                if (result.Body is null)
                {
                    merged.AddRange(state.LastGoodEvents);
                    if (state.LastGoodEvents.Count > 0) anyData = true;
                    continue;
                }

                // Body-hash dedupe: a provider that ignores If-None-Match still
                // returns the full body; if it's byte-identical to the last, skip
                // the re-parse (the expensive part) and reuse the events.
                string hash = Sha256Hex(result.Body);
                if (string.Equals(hash, state.BodyHash, StringComparison.Ordinal) && state.LastGoodEvents.Count > 0)
                {
                    state.Etag = result.Etag ?? state.Etag;
                    merged.AddRange(state.LastGoodEvents);
                    anyData = true;
                    continue;
                }

                DateTime windowStart = _now().AddDays(-CalendarFeedPolicy.WindowBackDays);
                DateTime windowEnd = _now().AddDays(CalendarFeedPolicy.WindowForwardDays);
                IReadOnlyList<CalendarEvent> events = CalendarEventParser.Parse(
                    result.Body, windowStart, windowEnd, feed.Label, feed.ColorHex);

                state.Etag = result.Etag ?? state.Etag;
                state.BodyHash = hash;
                state.LastGoodEvents = events;
                merged.AddRange(events);
                anyData = true;
                anyLive = true;
            }
            catch (CalDavAuthException ex)
            {
                // A rotated password: invalidate this feed's ETag so the next
                // poll re-resolves, and keep its last-good events in the merge.
                if (_logOnce.Changed($"auth:{key}"))
                    _log($"[CALENDAR] {feed.Label}: credential rejected, will re-resolve ({ex.Message})");
                lock (_stateGate) { state.Etag = null; }
                merged.AddRange(state.LastGoodEvents);
                if (state.LastGoodEvents.Count > 0) anyData = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolate the failure: the other feeds still update, and this
                // feed keeps its last-good events (ADR-0017). Log once per
                // message change.
                if (_logOnce.Changed($"err:{key}:{ex.Message}"))
                    _log($"[CALENDAR] {feed.Label}: fetch failed, keeping last-known events ({ex.Message})");
                merged.AddRange(state.LastGoodEvents);
                if (state.LastGoodEvents.Count > 0) anyData = true;
            }
        }

        CalendarSnapshot snapshot = new()
        {
            Events = merged.OrderBy(e => e.Start).ThenBy(e => e.Title, StringComparer.Ordinal).ToList(),
            HasData = anyData,
            IsLive = anyLive,
            LastUpdate = _now()
        };
        CalendarEventStore.UpdateFromDto(snapshot);
    }

    /// <summary>Drives the background poll loop with the current feed set.</summary>
    public void Start(IReadOnlyList<CalendarFeed> feeds)
    {
        _feeds = feeds;
        var token = _lifetime.Start(async ct =>
        {
            using var timer = new PeriodicTimer(_interval);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PollTickAsync(_feeds, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_logOnce.Changed($"tick:{ex.Message}"))
                        _log($"[CALENDAR] poll tick failed: {ex.Message}");
                }

                try { await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        });
        if (token.CanBeCanceled)
            _log($"[CALENDAR] polling started ({(int)_interval.TotalSeconds}s, background thread)");
    }

    public void Stop() => _lifetime.Stop();

    public void Dispose() => _lifetime.Dispose();

    // The live feed set handed to each tick (read under the gate so a settings
    // write-through swap is seen on the next tick without a restart).
    private volatile IReadOnlyList<CalendarFeed> _feeds = [];

    private string ResolveCredential(CalendarFeed feed)
        => feed is CalDavFeed ? _credentials.LoadPassword(feed.FeedId) ?? string.Empty : string.Empty;

    private static string Sha256Hex(string body)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));

    // One dedup rule for the per-feed log lines (the LogOnChange module).
    private readonly LogOnChange _logOnce = new();
}
