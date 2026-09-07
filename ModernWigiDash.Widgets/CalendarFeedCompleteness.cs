namespace ModernWigiDash.Widgets;

/// <summary>
/// The calendar feed's completeness rule (Widgets): the one owner of "what makes
/// a feed valid for polling", sitting next to the field definitions it validates
/// (<see cref="IcsUrlFeed"/>, <see cref="CalDavFeed"/>). An ics feed needs a
/// non-empty http(s) subscribe URL; a CalDAV feed needs a non-empty http(s)
/// server and a username. The label and feed id may be empty (they default at
/// fetch time). Every consumer that decides whether a feed is complete routes
/// through this instead of re-deriving the rule: the inspector's feed editor
/// (which drops incomplete rows before persisting) and any future producer-side
/// guard. The kind discriminator ("caldav") is spelled here once, so the
/// validity decision and the codec's shape decision cannot drift on what names
/// the CalDAV arm. (The former home was CalendarFeedEditorModel.IsComplete in
/// the App layer; the rule belongs beside the fields it validates.)
/// </summary>
internal static class CalendarFeedCompleteness
{
    /// <summary>The persisted kind spelling for the CalDAV arm (the one spelling
    /// shared with the codec's discriminator).</summary>
    public const string CalDavKind = "caldav";

    /// <summary>Whether an ics feed carries its required field: a non-empty
    /// http(s) subscribe URL.</summary>
    public static bool IsComplete(IcsUrlFeed feed) => IsAbsoluteHttpUrl(feed.Url);

    /// <summary>Whether a CalDAV feed carries its required fields: a non-empty
    /// http(s) server and a username. A blank username is incomplete (a
    /// whitespace-padded or empty account name would produce a degenerate auth
    /// header).</summary>
    public static bool IsComplete(CalDavFeed feed)
        => IsAbsoluteHttpUrl(feed.Server) && feed.Username.Trim().Length > 0;

    private static bool IsAbsoluteHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
               || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal));
}
