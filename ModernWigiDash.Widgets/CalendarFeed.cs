namespace ModernWigiDash.Widgets;

/// <summary>
/// A calendar feed the widget cluster polls: the discriminated union of the two
/// v1 source shapes. Both arms converge on the same fetcher seam later (the
/// producer sees one <see cref="CalendarFeed"/> and gets raw iCalendar text
/// back), so the rest of the cluster never branches on the source kind. The
/// identity (<see cref="Build"/>, <see cref="SameKey"/>) names "which feed is
/// this" for the store's dedupe and the producer's conditional-fetch stamp, and
/// deliberately carries NO secret: a CalDAV account's password lives
/// machine-local (the DPAPI-backed credential store), so a profile that travels
/// between machines re-resolves the credential against the same identity rather
/// than smuggling the secret across.
/// </summary>
internal abstract record CalendarFeed
{
    /// <summary>The stable per-feed id the profile persists (a short slug the
    /// user or the import assigns). Two feeds with different ids are different
    /// feeds even if every other field matches.</summary>
    public required string FeedId { get; init; }

    /// <summary>The display label shown next to events from this feed (the
    /// per-feed identity in the merged stream).</summary>
    public required string Label { get; init; }

    /// <summary>The per-feed color hex (the accent the layout tints its rows
    /// with), or empty when the feed declared none.</summary>
    public string ColorHex { get; init; } = string.Empty;

    /// <summary>True when the feed is enabled (a disabled feed is skipped by the
    /// producer but keeps its config so re-enabling needs no re-entry).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The source kind, as the persisted spelling the profile carries
    /// (the discriminator the deserializer reads before the arm-specific
    /// fields).</summary>
    public abstract string Kind { get; }

    /// <summary>
    /// Builds the feed's identity key: the non-secret config that defines "which
    /// feed is this". Fields are backslash-escaped and '|' -joined (the
    /// WeatherQueryKey rule) so a separator inside a field can never forge a
    /// colliding key. A change in any field yields a different key, which the
    /// store treats as a new feed and the producer as a fresh fetch.
    /// </summary>
    public abstract string Build();

    /// <summary>The single identity predicate: ordinal comparison, case is
    /// identity, null-safe on both sides (the WeatherQueryKey.SameKey rule).</summary>
    public static bool SameKey(string? left, string? right)
        => string.Equals(left, right, StringComparison.Ordinal);

    private protected static string EscapeField(string? value)
        => (value ?? "").Replace("\\", "\\\\").Replace("|", "\\|");
}

/// <summary>
/// A private .ics URL feed (Google, Outlook/Exchange, Nextcloud, Fastmail): the
/// provider's subscribe link returns an iCalendar body directly over HTTPS. The
/// identity is the URL itself (plus the feed id and label), so editing the URL
/// is an identity change and forces a fresh fetch.
/// </summary>
internal sealed record IcsUrlFeed : CalendarFeed
{
    /// <summary>The absolute https:// subscribe URL.</summary>
    public required string Url { get; init; }

    public override string Kind => "ics";

    public override string Build()
        => string.Join('|',
            "ics",
            EscapeField(FeedId),
            EscapeField(Label),
            EscapeField(ColorHex),
            EscapeField(Url));
}

/// <summary>
/// A CalDAV account feed (iCloud in v1): the producer discovers the account's
/// calendars via PROPFIND (RFC 4791) and pulls each calendar's .ics. The
/// identity is the server + principal + the selected calendar set, plus the feed
/// id and label. The username rides the identity (it names the account); the
/// password does NOT (it lives machine-local). Editing the server, principal, or
/// the selected calendars is an identity change and forces a fresh discovery.
/// </summary>
internal sealed record CalDavFeed : CalendarFeed
{
    /// <summary>The CalDAV server base (e.g. caldav.icloud.com).</summary>
    public required string Server { get; init; }

    /// <summary>The port (443 for iCloud).</summary>
    public int Port { get; init; } = 443;

    /// <summary>The principal path (e.g. /calendars/{username}/).</summary>
    public required string PrincipalPath { get; init; }

    /// <summary>The account username (rides the identity; names the account).</summary>
    public required string Username { get; init; }

    /// <summary>The selected calendar home-set paths to poll (empty = all
    /// discovered calendars).</summary>
    public IReadOnlyList<string> SelectedCalendars { get; init; } = [];

    public override string Kind => "caldav";

    public override string Build()
        => string.Join('|',
            "caldav",
            EscapeField(FeedId),
            EscapeField(Label),
            EscapeField(ColorHex),
            EscapeField(Server),
            Port.ToString(CultureInfo.InvariantCulture),
            EscapeField(PrincipalPath),
            EscapeField(Username),
            EscapeField(string.Join(';', SelectedCalendars)));
}
