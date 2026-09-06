namespace ModernWigiDash.Widgets;

/// <summary>One timed agenda row's display facts: the time string, the title,
/// the urgency tint (the accent the layout draws the row with), and the
/// meeting link (empty when the feed carried none) the tap-to-open action
/// routes through.</summary>
internal sealed record CalendarRow(string TimeText, string Title, bool IsUrgent, string Url);

/// <summary>The all-day tray's display facts: the label and the count of all-day
/// items in the window.</summary>
internal sealed record CalendarAllDayPill(string Label, int Count);

/// <summary>
/// The calendar widget's complete display facts at one instant: the hero (title
/// + countdown + live flag), the timed rows, the all-day pill, the staleness
/// hint (empty when live), and the data fact. The render methods lay these out;
/// the rules that produced them are assertable here without pixels.
/// </summary>
internal sealed record CalendarDisplay(
    string HeroTitle,
    string HeroCountdown,
    bool HeroIsLive,
    IReadOnlyList<CalendarRow> Rows,
    CalendarAllDayPill AllDayPill,
    string StalenessHint,
    bool HasData);

/// <summary>
/// Everything the calendar widget draws that is a *fact* about the snapshot at
/// a given instant: the hero (the active or next event + its countdown), the
/// timed rows, the all-day pill, and the unavailable-state hint. The render
/// methods become thin adapters that lay these out -- the display rules are
/// assertable without pixels. The "now" accuracy rule lives here: the hero and
/// countdown are recomputed from the injected clock on every build, so a tick
/// that runs a second late still shows the correct remaining time. All number
/// formatting routes through <see cref="DisplayFormat"/> (the invariant-culture
/// contract).
/// </summary>
internal static class CalendarPresentation
{
    /// <summary>The urgency threshold: an event starting within this many
    /// minutes reads as "approaching" (amber); one already started reads as
    /// "live" (red).</summary>
    private const int UrgencyWindowMinutes = 30;

    /// <summary>
    /// Builds the widget's display facts from one snapshot at the placement size
    /// and the current instant. The hero is the currently-active event (started,
    /// not yet ended) when one exists, else the next upcoming event; the
    /// countdown is its remaining time (or elapsed, when live). The timed rows
    /// are the next events after the hero (up to <paramref name="timedRows"/>,
    /// excluding the hero itself and any all-day items). The all-day pill
    /// aggregates the date-only items. A no-data snapshot yields the named
    /// unavailable display (ADR-0017): a staleness hint when the last-good cache
    /// is rendering during an outage, an empty-agenda hint otherwise.
    /// </summary>
    /// <param name="snapshot">The merged calendar snapshot (may be null/empty).</param>
    /// <param name="now">The current machine-local instant (the "now" accuracy
    /// seam; the widget passes its per-tick clock read).</param>
    /// <param name="timedRows">How many timed rows to show (1-3, resolved
    /// through CalendarFeedPolicy).</param>
    public static CalendarDisplay Build(CalendarSnapshot? snapshot, DateTime now, int timedRows)
    {
        if (snapshot is null || !snapshot.HasData)
            return UnavailableDisplay(snapshot);

        List<CalendarEvent> timed = snapshot.Events
            .Where(e => !e.IsAllDay)
            .OrderBy(e => e.Start)
            .ToList();
        List<CalendarEvent> allDay = snapshot.Events
            .Where(e => e.IsAllDay)
            .OrderBy(e => e.Start)
            .ToList();

        // The hero: the active event (started, not ended) when one exists, else
        // the next upcoming event. Recomputed from `now` every build. CalendarEvent
        // is a struct, so FirstOrDefault yields default(T) when absent; track
        // presence with a flag instead of a nullable.
        CalendarEvent hero = default;
        bool hasHero = false;
        foreach (CalendarEvent e in timed)
        {
            if (e.Start <= now && now < e.End)
            {
                hero = e;
                hasHero = true;
                break;
            }
        }
        if (!hasHero)
        {
            foreach (CalendarEvent e in timed)
            {
                if (e.Start > now)
                {
                    hero = e;
                    hasHero = true;
                    break;
                }
            }
        }

        string heroTitle = string.Empty;
        string heroCountdown = string.Empty;
        bool heroIsLive = false;
        var rows = new List<CalendarRow>();

        if (hasHero)
        {
            heroIsLive = hero.Start <= now && now < hero.End;
            heroTitle = HeroTitle(hero.Title);
            heroCountdown = heroIsLive
                ? ElapsedText(now - hero.Start)
                : RemainingText(hero.Start - now);
            rows.Add(new CalendarRow(FormatTime(hero.Start), heroTitle, heroIsLive, hero.Url));
        }

        int slots = Math.Max(1, Math.Min(timedRows, CalendarFeedPolicy.MaxTimedRows));
        foreach (CalendarEvent e in timed.Where(x => !hasHero || !x.Equals(hero)).Take(slots))
        {
            bool urgent = (e.Start - now).TotalMinutes is > 0 and <= UrgencyWindowMinutes;
            rows.Add(new CalendarRow(FormatTime(e.Start), Truncate(e.Title, 28), urgent, e.Url));
        }

        CalendarAllDayPill pill = allDay.Count > 0
            ? new CalendarAllDayPill(AllDayLabel(allDay[0]), allDay.Count)
            : new CalendarAllDayPill(string.Empty, 0);

        return new CalendarDisplay(
            HeroTitle: heroTitle,
            HeroCountdown: heroCountdown,
            HeroIsLive: heroIsLive,
            Rows: rows,
            AllDayPill: pill,
            StalenessHint: string.Empty,
            HasData: true);
    }

    // --- helpers -------------------------------------------------------------

    private static CalendarDisplay UnavailableDisplay(CalendarSnapshot? snapshot)
    {
        // ADR-0017: a dropout renders the last-known agenda with a staleness
        // hint; a never-fetched state renders the empty-agenda hint.
        string hint = snapshot is { HasData: false } && snapshot.LastUpdate != default
            ? "Last known schedule"
            : "No calendars configured";
        return new CalendarDisplay(
            HeroTitle: string.Empty,
            HeroCountdown: string.Empty,
            HeroIsLive: false,
            Rows: [],
            AllDayPill: new CalendarAllDayPill(string.Empty, 0),
            StalenessHint: hint,
            HasData: false);
    }

    private static string HeroTitle(string title) => string.IsNullOrWhiteSpace(title) ? "Untitled" : Truncate(title, 40);

    private static string FormatTime(DateTime t)
        => $"{t.Hour:D2}:{t.Minute:D2}";

    private static string RemainingText(TimeSpan span)
    {
        if (span.TotalHours >= 24)
            return $"in {(int)span.TotalDays}d";
        int hours = (int)span.TotalHours;
        int minutes = span.Minutes;
        if (hours >= 1)
            return $"in {hours}h {minutes:D2}m";
        return $"in {minutes}m";
    }

    private static string ElapsedText(TimeSpan span)
    {
        int hours = (int)span.TotalHours;
        int minutes = span.Minutes;
        if (hours >= 1)
            return $"+{hours}h {minutes:D2}m";
        return $"+{minutes}m";
    }

    private static string AllDayLabel(CalendarEvent e)
        => string.IsNullOrWhiteSpace(e.Title) ? "All day" : Truncate(e.Title, 28);

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "\u2026";
}
