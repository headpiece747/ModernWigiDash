namespace ModernWigiDash.Widgets;

/// <summary>
/// The kind-routing calendar fetcher: holds one .ics-URL fetcher and one CalDAV
/// fetcher (both over the same shared HttpClient) and dispatches each feed to
/// its kind's fetcher. The producer's single <c>Func&lt;IFeedFetcher&gt;</c> seam
/// stays kind-agnostic -- it hands every feed to this dispatcher, which picks
/// the right leg. A mixed profile (some public .ics feeds, some CalDAV accounts)
/// therefore needs no per-kind wiring at the producer or the widget.
/// </summary>
internal sealed class DispatchingFeedFetcher : IFeedFetcher
{
    private readonly IcsUrlFetcher _ics;
    private readonly CalDavFetcher _calDav;

    /// <param name="http">The shared HttpClient factory (one process-wide
    /// client both legs share).</param>
    public DispatchingFeedFetcher(Func<HttpClient> http)
    {
        _ics = new IcsUrlFetcher(http);
        _calDav = new CalDavFetcher(http);
    }

    public Task<FeedFetchResult> FetchAsync(
        CalendarFeed feed,
        string credential,
        string? previousEtag,
        CancellationToken cancellationToken)
        => feed is CalDavFeed
            ? _calDav.FetchAsync(feed, credential, previousEtag, cancellationToken)
            : _ics.FetchAsync(feed, credential, previousEtag, cancellationToken);
}
