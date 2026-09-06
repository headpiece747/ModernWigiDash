namespace ModernWigiDash.Widgets;

/// <summary>
/// The calendar cluster's feed-policy module: the one owner of the tuning
/// constants every fetcher, store, and presentation module reads. One spelling
/// per rule so a change edits exactly one site and the pins reference the same
/// constant the production path uses (the WeatherForecastLimits image): the poll
/// cadence choices, the recurrence-expansion window, the timed-row range, and
/// the raw-feed size bound.
/// </summary>
internal static class CalendarFeedPolicy
{
    /// <summary>The poll-cadence choices in display order (minutes), as the
    /// inspector's choice values (never display strings the dialog re-maps).
    /// 30 is the default: frequent enough that a meeting starting soon appears,
    /// quiet enough to be polite to a provider that rate-limits.</summary>
    public static readonly int[] PollIntervalMinutes = [15, 30, 60];

    /// <summary>The default poll interval (minutes) when a feed declares none.</summary>
    public const int DefaultPollIntervalMinutes = 30;

    /// <summary>How far back from "now" the expansion window starts (days): one
    /// day, so an event that started yesterday and runs long still shows as
    /// active today.</summary>
    public const int WindowBackDays = 1;

    /// <summary>How far ahead of "now" the expansion window ends (days): the
    /// default agenda horizon. Bounded so a years-old work calendar never
    /// expands unbounded recurrences on every poll.</summary>
    public const int WindowForwardDays = 14;

    /// <summary>The minimum timed rows the layout can show (the hero always
    /// counts separately).</summary>
    public const int MinTimedRows = 1;

    /// <summary>The maximum timed rows the layout can show.</summary>
    public const int MaxTimedRows = 3;

    /// <summary>The default timed-row count when a profile omits it.</summary>
    public const int DefaultTimedRows = 2;

    /// <summary>The raw-feed size bound (bytes): a body larger than this is
    /// refused at the file boundary before any parse (a hostile or runaway feed
    /// degrades to "no events" instead of a multi-GB read, the pre-read-reject
    /// shape the profile-import guard already owns).</summary>
    public const int MaxFeedBytes = 2 * 1024 * 1024;

    /// <summary>Resolves a persisted poll interval to a valid choice: a value in
    /// <see cref="PollIntervalMinutes"/> passes through; anything else (absent,
    /// hand-edited, out of range) degrades to the default. The profile is a
    /// hand-editable artifact, so the raw value is a string/int and this policy
    /// is the single resolution site (the CloseBehaviorPolicy.Resolve image).</summary>
    public static int ResolvePollInterval(int? minutes)
        => minutes is { } m && Array.IndexOf(PollIntervalMinutes, m) >= 0 ? m : DefaultPollIntervalMinutes;

    /// <summary>Clamps a persisted timed-row count into the valid range: a value
    /// inside [Min, Max] passes through; anything else degrades to the default.
    /// A hand-edited profile can never smuggle in a row count the layout cannot
    /// draw.</summary>
    public static int ResolveTimedRows(int? rows)
        => rows is { } r && r >= MinTimedRows && r <= MaxTimedRows ? r : DefaultTimedRows;
}
