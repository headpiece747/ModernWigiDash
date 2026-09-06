namespace ModernWigiDash.Widgets;

/// <summary>
/// A single concrete calendar occurrence in the machine's local time zone: one
/// slot on the agenda, whether it came from a plain event, an expanded RRULE
/// instance, or an all-day item. This is the normalized shape the whole widget
/// cluster consumes, so the RFC 5545 specifics (RRULE, EXDATE, VTIMEZONE,
/// floating time) stay sealed inside <see cref="CalendarEventParser"/> and no
/// downstream module ever sees a raw iCalendar type. All times are local
/// <see cref="DateTime"/> (Kind Unspecified) already converted to the machine's
/// zone, so the presentation layer formats them without re-doing any timezone
/// math.
/// </summary>
internal readonly record struct CalendarEvent
{
    /// <summary>The display title (SUMMARY); empty when the feed omitted it.</summary>
    public string Title { get; init; }

    /// <summary>Local start instant.</summary>
    public DateTime Start { get; init; }

    /// <summary>Local end instant; for an all-day item this is the day after
    /// <see cref="Start"/> (iCalendar all-day events are exclusive-end).</summary>
    public DateTime End { get; init; }

    /// <summary>True for all-day items (birthdays, holidays): the layout renders
    /// these in the separate tray, not the timed list.</summary>
    public bool IsAllDay { get; init; }

    /// <summary>The location (LOCATION); empty when absent.</summary>
    public string Location { get; init; }

    /// <summary>The meeting link (URL property / description link); empty when
    /// there is none. Carried for the glyph and the optional shell-open, never
    /// parsed further here.</summary>
    public string Url { get; init; }

    /// <summary>The stable series identity (UID); lets the store dedupe a moved
    /// occurrence against its master and keep expansion stable across polls.</summary>
    public string SeriesId { get; init; }

    /// <summary>The originating feed label, so the merged stream can show which
    /// calendar an event came from.</summary>
    public string FeedLabel { get; init; }

    /// <summary>The originating feed color hex (the per-feed identity), or empty
    /// when the feed declared none.</summary>
    public string FeedColorHex { get; init; }
}
