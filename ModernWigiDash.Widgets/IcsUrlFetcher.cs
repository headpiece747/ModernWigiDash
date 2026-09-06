using System.Text;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The outcome of one calendar-feed fetch: the raw iCalendar body (or null when
/// nothing new arrived) plus the validator token the next poll sends back so an
/// unchanged feed costs a 304 instead of a full body. A null <see cref="Body"/>
/// with a non-null <see cref="Etag"/> is the "not modified" verdict; a null
/// <see cref="Etag"/> means the provider gave no validator (the producer then
/// falls back to its local body-hash dedupe).
/// </summary>
internal sealed record FeedFetchResult(string? Body, string? Etag);

/// <summary>
/// The calendar-feed fetch seam: one door the store/producer polls, hiding the
/// source kind behind a single call. Both v1 arms (<c>IcsUrlFetcher</c> over
/// HTTPS and <c>CalDavFetcher</c> over CalDAV PROPFIND) return raw iCalendar
/// text through this shape, so the rest of the cluster never branches on the
/// source kind. The credential half is injected per call (a CalDAV password is
/// resolved from the machine-local store at fetch time, never carried on the
/// feed identity), and every leg applies the size-bound pre-read-reject and a
/// bounded body read (the WeatherGeocoder.ReadBoundedAsync rule), so a hostile
/// or runaway feed degrades to a failure instead of a multi-GB read.
/// </summary>
internal interface IFeedFetcher
{
    /// <summary>
    /// Fetches the feed's current iCalendar body. <paramref name="feed"/> names
    /// the source (and, for CalDAV, the selected calendars); <paramref
    /// name="credential"/> is the resolved secret (the CalDAV password, empty
    /// for an unauthenticated .ics URL); <paramref name="previousEtag"/> is the
    /// validator from the last successful fetch (sent as If-None-Match when the
    /// provider supports it, yielding a not-modified verdict). Returns the body
    /// plus the new validator; throws on a transport failure (the producer logs
    /// and keeps its last-good snapshot).
    /// </summary>
    Task<FeedFetchResult> FetchAsync(
        CalendarFeed feed,
        string credential,
        string? previousEtag,
        CancellationToken cancellationToken);
}

/// <summary>
/// The private-.ics-URL fetcher: one HTTPS GET of the provider's subscribe link
/// (Google, Outlook/Exchange, Nextcloud, Fastmail). Applies the size-bound
/// pre-read-reject and the bounded body read, and honors If-None-Match /
/// If-Modified-Since best-effort (a 304 yields a not-modified verdict; a
/// provider that ignores the validators still returns the body, which the
/// producer's body-hash dedupe then treats as unchanged).
/// </summary>
internal sealed class IcsUrlFetcher : IFeedFetcher
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<HttpClient> _http;
    private readonly TimeSpan _timeout;

    public IcsUrlFetcher(Func<HttpClient> http, TimeSpan? timeout = null)
    {
        _http = http;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<FeedFetchResult> FetchAsync(
        CalendarFeed feed,
        string credential,
        string? previousEtag,
        CancellationToken cancellationToken)
    {
        if (feed is not IcsUrlFeed ics)
            throw new ArgumentException("IcsUrlFetcher only fetches IcsUrlFeed feeds.", nameof(feed));

        using var request = new HttpRequestMessage(HttpMethod.Get, ics.Url);
        // Best-effort conditional fetch: send the validator when we have one. A
        // provider that honors it answers 304 (no body); one that ignores it
        // answers 200 with the body, which the producer's hash dedupe absorbs.
        if (!string.IsNullOrEmpty(previousEtag))
            request.Headers.TryAddWithoutValidation("If-None-Match", previousEtag);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            using var response = await _http().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);

            if (response.StatusCode == System.Net.HttpStatusCode.NotModified)
                return new FeedFetchResult(null, previousEtag);

            response.EnsureSuccessStatusCode();

            long? declared = response.Content.Headers.ContentLength;
            if (declared > CalendarFeedPolicy.MaxFeedBytes)
                throw new HttpRequestException($"HTTP response exceeds the {CalendarFeedPolicy.MaxFeedBytes} byte bound");

            long cap = declared is > 0 ? declared.Value : CalendarFeedPolicy.MaxFeedBytes;
            using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            byte[] body = await BoundedRead.ReadAsync(stream, cap, timeoutCts.Token).ConfigureAwait(false);

            string? etag = response.Headers.ETag?.Tag;
            return new FeedFetchResult(Encoding.UTF8.GetString(body), etag);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            double effectiveSeconds = _timeout.TotalSeconds;
            throw new TimeoutException($"calendar feed exceeded the {effectiveSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s deadline");
        }
    }
}
