using System.Net;
using System.Text;
using System.Xml.Linq;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The CalDAV account fetcher (iCloud in v1): discovers the account's calendars
/// via PROPFIND (RFC 4791) and pulls each selected calendar's iCalendar body.
/// The discovery ladder is: PROPFIND the principal for its calendar-home-set,
/// then PROPFIND the home-set for the calendar resources, then GET each
/// selected calendar's .ics URL. Every leg applies the size-bound pre-read-
/// reject and the bounded body read (the WeatherGeocoder rule). The password is
/// injected per call (resolved from the machine-local credential store), never
/// carried on the feed identity or logged. A 401/403 invalidates the producer's
/// discovery cache so the next poll re-prompts for the credential.
/// </summary>
internal sealed class CalDavFetcher : IFeedFetcher
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<HttpClient> _http;
    private readonly TimeSpan _timeout;

    public CalDavFetcher(Func<HttpClient> http, TimeSpan? timeout = null)
    {
        _http = http;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The one seam the producer uses to learn which calendars an
    /// account exposes (for the inspector's calendar picker): a PROPFIND of the
    /// principal's home-set, returning each calendar's display name and .ics
    /// URL. Throws on a transport failure; a 401/403 surfaces as a
    /// <see cref="CalDavAuthException"/> so the caller can invalidate the
    /// credential cache.</summary>
    public async Task<IReadOnlyList<CalDavCalendarInfo>> DiscoverAsync(
        CalDavFeed feed, string credential, CancellationToken cancellationToken)
    {
        Uri homeSet = await FindHomeSetAsync(feed, credential, cancellationToken).ConfigureAwait(false);
        XDocument propfind = await SendPropFindAsync(homeSet.ToString(), CalendarListPropFind, feed.Username, credential, cancellationToken).ConfigureAwait(false);

        var results = new List<CalDavCalendarInfo>();
        foreach (XElement response in propfind.Descendants().Where(e => string.Equals(e.Name.LocalName, "response", StringComparison.Ordinal)))
        {
            string? href = response.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "href", StringComparison.Ordinal))?.Value;
            if (string.IsNullOrEmpty(href))
                continue;

            string displayName = response.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "displayname", StringComparison.Ordinal))?.Value
                ?? response.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, "cn", StringComparison.Ordinal))?.Value
                ?? Path.GetFileName(href.TrimEnd('/'));
            string url = MakeAbsolute(homeSet, href).ToString();
            results.Add(new CalDavCalendarInfo(displayName, url));
        }
        return results.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<FeedFetchResult> FetchAsync(
        CalendarFeed feed,
        string credential,
        string? previousEtag,
        CancellationToken cancellationToken)
    {
        if (feed is not CalDavFeed cal)
            throw new ArgumentException("CalDavFetcher only fetches CalDavFeed feeds.", nameof(feed));

        // Resolve the calendar URLs to poll: the selected set when named, else
        // every discovered calendar. Discovery is cached by the producer (the
        // feed's identity includes the selected set, so a change forces a
        // re-discovery); here we discover fresh because this seam has no cache.
        IReadOnlyList<string> urls;
        if (cal.SelectedCalendars.Count > 0)
        {
            urls = cal.SelectedCalendars;
        }
        else
        {
            IReadOnlyList<CalDavCalendarInfo> discovered = await DiscoverAsync(cal, credential, cancellationToken).ConfigureAwait(false);
            urls = discovered.Select(c => c.Url).ToList();
        }

        var bodies = new List<string>(urls.Count);
        string? lastEtag = null;
        bool anyModified = false;
        foreach (string url in urls)
        {
            FeedFetchResult one = await FetchOneCalendarAsync(url, cal.Username, credential, previousEtag, cancellationToken).ConfigureAwait(false);
            if (one.Body is not null)
            {
                bodies.Add(one.Body);
                anyModified = true;
            }
            if (one.Etag is not null)
                lastEtag = one.Etag;
        }

        // A multi-calendar feed has no single provider ETag; the combined body
        // is what the producer's hash dedupe keys on. Return null when nothing
        // changed so the producer keeps its snapshot without re-parsing.
        if (!anyModified)
            return new FeedFetchResult(null, lastEtag);

        return new FeedFetchResult(string.Join("\r\n", bodies), lastEtag);
    }

    // --- legs ----------------------------------------------------------------

    private async Task<Uri> FindHomeSetAsync(CalDavFeed feed, string credential, CancellationToken ct)
    {
        Uri principal = BuildUri(feed, feed.PrincipalPath);
        XDocument doc = await SendPropFindAsync(principal.ToString(), HomeSetPropFind, feed.Username, credential, ct).ConfigureAwait(false);

        string? homeHref = FirstElementText(doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "response", StringComparison.Ordinal)),
            "calendar-home-set", "href");
        if (homeHref is null)
            throw new HttpRequestException("CalDAV principal did not report a calendar-home-set");

        return MakeAbsolute(principal, homeHref);
    }

    private async Task<FeedFetchResult> FetchOneCalendarAsync(
        string url, string username, string credential, string? previousEtag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyBasicAuth(request, username, credential);
        if (!string.IsNullOrEmpty(previousEtag))
            request.Headers.TryAddWithoutValidation("If-None-Match", previousEtag);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            using var response = await _http().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return new FeedFetchResult(null, previousEtag);
            ThrowOnAuthFailure(response);
            response.EnsureSuccessStatusCode();

            long? declared = response.Content.Headers.ContentLength;
            if (declared > CalendarFeedPolicy.MaxFeedBytes)
                throw new HttpRequestException($"CalDAV response exceeds the {CalendarFeedPolicy.MaxFeedBytes} byte bound");

            long cap = declared is > 0 ? declared.Value : CalendarFeedPolicy.MaxFeedBytes;
            using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            byte[] body = await BoundedRead.ReadAsync(stream, cap, timeoutCts.Token).ConfigureAwait(false);
            return new FeedFetchResult(Encoding.UTF8.GetString(body), response.Headers.ETag?.Tag);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            double effectiveSeconds = _timeout.TotalSeconds;
            throw new TimeoutException($"CalDAV leg exceeded the {effectiveSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s deadline");
        }
    }

    private async Task<XDocument> SendPropFindAsync(
        string url, XDocument body, string username, string credential, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), url);
        ApplyBasicAuth(request, username, credential);
        request.Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            using var response = await _http().SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token).ConfigureAwait(false);
            ThrowOnAuthFailure(response);
            response.EnsureSuccessStatusCode();

            long? declared = response.Content.Headers.ContentLength;
            if (declared > CalendarFeedPolicy.MaxFeedBytes)
                throw new HttpRequestException($"CalDAV PROPFIND exceeds the {CalendarFeedPolicy.MaxFeedBytes} byte bound");

            long cap = declared is > 0 ? declared.Value : CalendarFeedPolicy.MaxFeedBytes;
            using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            byte[] bodyBytes = await BoundedRead.ReadAsync(stream, cap, timeoutCts.Token).ConfigureAwait(false);
            return XDocument.Load(new MemoryStream(bodyBytes));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            double effectiveSeconds = _timeout.TotalSeconds;
            throw new TimeoutException($"CalDAV PROPFIND exceeded the {effectiveSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s deadline");
        }
    }

    // --- helpers -------------------------------------------------------------

    private static void ApplyBasicAuth(HttpRequestMessage request, string username, string credential)
    {
        // Refuse to send credentials over cleartext HTTP: a Basic-auth header is
        // base64(user:pass), readable by any on-path observer. CalDAV is https
        // in practice; http is only for an explicit local test server that has
        // no credential to protect.
        if (request.RequestUri is { } uri && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && !string.IsNullOrEmpty(credential))
            throw new HttpRequestException("Refusing to send CalDAV credentials over cleartext HTTP; use an HTTPS endpoint.");

        // Trim the username so a whitespace-padded value from a hand-edited
        // profile cannot produce a degenerate Basic-auth header.
        string trimmedUser = username.Trim();
        if (trimmedUser.Length == 0)
            throw new ArgumentException("CalDAV username must not be blank.", nameof(username));

        string userPass = $"{trimmedUser}:{credential}";
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(userPass)));
    }

    private static void ThrowOnAuthFailure(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new CalDavAuthException("CalDAV credentials were rejected (401/403); the credential cache is invalidated.");
    }

    private static Uri BuildUri(CalDavFeed feed, string path)
    {
        // CalDAV is https in practice (iCloud is 443); http is allowed only for
        // an explicit non-443/80 test server. The linter flags the literal; the
        // production path is always https.
#pragma warning disable S5332 // http only for an explicit test-server port
        string scheme = feed.Port == 80 ? "http" : "https";
#pragma warning restore S5332
        return new Uri($"{scheme}://{feed.Server}:{feed.Port}{path}");
    }

    private static Uri MakeAbsolute(Uri baseUri, string href)
    {
        Uri abs = Uri.TryCreate(baseUri, href, out var resolved) ? resolved : new Uri(href, UriKind.Absolute);

        // A server-controlled href must not redirect the request to a different
        // origin: an attacker CalDAV server could return an href pointing at an
        // arbitrary host, turning the client into an SSRF oracle and replaying
        // the user's Basic-auth header to a third party. Verify the resolved URL
        // stays within the base URI's scheme + authority.
        if (!string.Equals(abs.Scheme, baseUri.Scheme, StringComparison.Ordinal) ||
            !string.Equals(abs.Authority, baseUri.Authority, StringComparison.Ordinal))
            throw new HttpRequestException($"CalDAV href resolves to a different origin ({abs.Authority}); refusing to fetch.");

        return abs;
    }

    private static string? FirstElementText(IEnumerable<XElement> responses, string propName, string childName)
    {
        foreach (XElement response in responses)
        {
            // The property element may be in CalDavNs (urn:ietf:params:xml:ns:caldav)
            // or DavNs (DAV:), and sits inside <prop> under <propstat>.
            XElement? propEl = response.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, propName, StringComparison.Ordinal));
            if (propEl is null)
                continue;

            // Prefer the nested child element (e.g. <href> inside <calendar-home-set>)
            string? child = propEl.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, childName, StringComparison.Ordinal))?.Value;
            if (!string.IsNullOrEmpty(child))
                return child;

            if (!string.IsNullOrEmpty(propEl.Value))
                return propEl.Value;
        }
        return null;
    }

    // The two PROPFIND request bodies (RFC 4791). Kept as constants so the wire
    // shape is spelled once and testable.
    internal static readonly XDocument HomeSetPropFind = XDocument.Parse("""
<?xml version="1.0" encoding="utf-8" ?>
<propfind xmlns="DAV:">
  <prop>
    <current-user-principal />
    <calendar-home-set xmlns="urn:ietf:params:xml:ns:caldav" />
  </prop>
</propfind>
""");

    internal static readonly XDocument CalendarListPropFind = XDocument.Parse("""
<?xml version="1.0" encoding="utf-8" ?>
<propfind xmlns="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
  <prop>
    <displayname />
    <c:calendar-data />
  </prop>
</propfind>
""");
}

/// <summary>One discovered CalDAV calendar: its display name and .ics URL.</summary>
internal sealed record CalDavCalendarInfo(string DisplayName, string Url);

/// <summary>
/// A CalDAV credential rejection (401/403): the producer catches this to
/// invalidate the discovery/credential cache so the next poll re-resolves the
/// password (a rotated app-specific password must not wedge the feed forever).
/// Internal by design: it crosses only the fetcher→producer boundary inside the
/// Widgets assembly, never a public API surface.
/// </summary>
#pragma warning disable S3871 // internal exception is deliberate (Widgets-internal seam)
internal sealed class CalDavAuthException(string message) : Exception(message);
#pragma warning restore S3871
