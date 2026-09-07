using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// One feed row in the calendar feed editor: the editable fields the user sees
/// (kind, label, connection details, enabled). This is a plain data shape the
/// renderer binds to -- deliberately NOT the widget's internal
/// <c>CalendarFeed</c> record, so the App layer needs no internals access. The
/// <see cref="CalendarFeedEditorModel"/> owns the rules that turn these drafts
/// into the persisted <c>FeedsJson</c> value and back.
/// </summary>
internal sealed class CalendarFeedDraft
{
    /// <summary>The source kind spelling: "ics" or "caldav".</summary>
    public string Kind { get; set; } = "ics";

    /// <summary>The stable per-feed id (a short slug).</summary>
    public string FeedId { get; set; } = "";

    /// <summary>The display label shown next to events from this feed.</summary>
    public string Label { get; set; } = "";

    /// <summary>The .ics subscribe URL (ics feeds).</summary>
    public string Url { get; set; } = "";

    /// <summary>The CalDAV server base (caldav feeds).</summary>
    public string Server { get; set; } = "";

    /// <summary>The CalDAV port (caldav feeds).</summary>
    public int Port { get; set; } = 443;

    /// <summary>The CalDAV principal path (caldav feeds).</summary>
    public string PrincipalPath { get; set; } = "";

    /// <summary>The CalDAV account username (caldav feeds).</summary>
    public string Username { get; set; } = "";

    /// <summary>Whether the feed is enabled.</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// The calendar feed editor's decision rules -- the feed-list editing behavior
/// of the inspector as data, testable without WPF. The editor shows one row per
/// feed plus an "add" button; every mutation rebuilds the whole list and commits
/// it as a single <c>FeedsJson</c> string through the inspector's write-back
/// funnel. The model owns the two directions of the round-trip (parse the
/// persisted JSON into drafts for display, serialize drafts back to JSON) and
/// the per-draft completeness rule (a feed is only serialized when its required
/// fields are filled, so a half-entered row never persists a broken feed).
/// </summary>
internal static class CalendarFeedEditorModel
{
    /// <summary>Parses the persisted <c>FeedsJson</c> value into editable
    /// drafts by routing through the shared <see cref="CalendarFeedsCodec"/>
    /// owner (the one spelling of the persisted shape) and mapping its typed
    /// records to display drafts. A malformed or absent value yields an empty
    /// list (the editor starts blank), never a throw.</summary>
    public static IReadOnlyList<CalendarFeedDraft> Parse(string? feedsJson)
    {
        List<CalendarFeedDraft> drafts = [];
        foreach (CalendarFeed feed in CalendarFeedsCodec.Parse(feedsJson))
        {
            switch (feed)
            {
                case CalDavFeed dav:
                    drafts.Add(new CalendarFeedDraft
                    {
                        Kind = "caldav",
                        FeedId = dav.FeedId,
                        Label = dav.Label,
                        Server = dav.Server,
                        Port = dav.Port,
                        PrincipalPath = dav.PrincipalPath,
                        Username = dav.Username,
                        Enabled = dav.Enabled,
                    });
                    break;
                case IcsUrlFeed ics:
                    drafts.Add(new CalendarFeedDraft
                    {
                        Kind = "ics",
                        FeedId = ics.FeedId,
                        Label = ics.Label,
                        Url = ics.Url,
                        Enabled = ics.Enabled,
                    });
                    break;
            }
        }
        return drafts;
    }

    /// <summary>Serializes the editable drafts back to the persisted
    /// <c>FeedsJson</c> value. Incomplete feeds (missing their required fields)
    /// are dropped, so a half-entered row never persists a broken feed; an all-
    /// incomplete list serializes to an empty array (the widget renders its
    /// unavailable display).</summary>
    public static string Serialize(IReadOnlyList<CalendarFeedDraft> drafts)
    {
        // Map the complete drafts to typed feed records, then hand the whole
        // list to the shared codec owner for the one spelling of the persisted
        // shape (the kind discriminator and the arm-specific fields).
        List<CalendarFeed> feeds = [];
        foreach (CalendarFeedDraft d in drafts)
        {
            if (!IsComplete(d))
                continue;

            feeds.Add(string.Equals(d.Kind, "caldav", StringComparison.Ordinal)
                ? new CalDavFeed
                {
                    FeedId = d.FeedId,
                    Label = d.Label,
                    Server = d.Server,
                    Port = d.Port,
                    PrincipalPath = d.PrincipalPath,
                    Username = d.Username,
                    Enabled = d.Enabled,
                }
                : new IcsUrlFeed
                {
                    FeedId = d.FeedId,
                    Label = d.Label,
                    Url = d.Url,
                    Enabled = d.Enabled,
                });
        }
        return CalendarFeedsCodec.Serialize(feeds);
    }

    /// <summary>Whether a draft carries every field its kind requires: an ics
    /// feed needs a non-empty http(s) URL; a caldav feed needs a non-empty
    /// http(s) server and a username. The label and feed id may be empty (they
    /// default at fetch time).</summary>
    public static bool IsComplete(CalendarFeedDraft d)
        => string.Equals(d.Kind, "caldav", StringComparison.Ordinal)
            ? IsAbsoluteHttpUrl(d.Server) && d.Username.Trim().Length > 0
            : IsAbsoluteHttpUrl(d.Url);

    private static bool IsAbsoluteHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal));
}
