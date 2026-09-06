namespace ModernWigiDash.Widgets;

/// <summary>
/// The calendar cluster's in-process snapshot: the merged, normalized event
/// list from every enabled feed, plus the facts the widget needs to render its
/// unavailable state (ADR-0017) instead of frozen or empty data. This is the
/// shape the static <see cref="CalendarEventStore"/> caches and the widget reads
/// on the render thread. All times are machine-local (the parser normalized
/// them), so the presentation layer formats without re-doing timezone math.
/// </summary>
internal sealed record CalendarSnapshot
{
    /// <summary>The merged occurrences across all enabled feeds, sorted by start.</summary>
    public IReadOnlyList<CalendarEvent> Events { get; init; } = [];

    /// <summary>True when at least one feed produced data (a feed that fetched
    /// successfully but has no events in the window is still "has data": the
    /// agenda is genuinely empty, not a dropout).</summary>
    public bool HasData { get; init; }

    /// <summary>True when the snapshot reflects a live fetch (as opposed to the
    /// last-good cache rendered during an outage). The widget uses this to show
    /// the "last updated" staleness hint rather than pretending the data is
    /// current.</summary>
    public bool IsLive { get; init; }

    /// <summary>The producer timestamp for this snapshot (the store's freshness
    /// window compares against it).</summary>
    public DateTime LastUpdate { get; init; }

    /// <summary>The empty/unavailable sentinel: no events, no data, stale by
    /// construction (LastUpdate default).</summary>
    public static readonly CalendarSnapshot Empty = new()
    {
        Events = [],
        HasData = false,
        IsLive = false,
        LastUpdate = default
    };
}

/// <summary>
/// In-process cache of the latest calendar snapshot polled by the
/// <see cref="CalendarFeedProducer"/>. The producer calls
/// <see cref="UpdateFromDto"/> after each successful poll; widgets read the
/// cached snapshot on the render thread via <see cref="TryReadFresh"/>. The
/// store owns the staleness decision -- consumers cannot skip it. The
/// staleness window is generous (the last-good snapshot stays renderable across
/// a provider outage, ADR-0017): a dropout renders the last known agenda with a
/// staleness hint, not a blank panel. One instance of the shared
/// <see cref="TelemetryStoreFacade{TRecord}"/> (the LhmSensorStore /
/// FrameTimeStore image).
/// </summary>
internal static class CalendarEventStore
{
    // Generous on purpose: the calendar polls at 15-60 min cadence, so a fresh
    // snapshot is minutes old by the next poll. The window only gates "did the
    // producer stop entirely" (app suspending, all feeds dead), not per-poll
    // freshness -- the widget's own staleness hint covers the latter.
    private const int StalenessWindowSeconds = 3 * 60 * 60;

    private static readonly TelemetryStoreFacade<CalendarSnapshot> Facade = new(
        CalendarSnapshot.Empty,
        defaultMaxAge: TimeSpan.FromSeconds(StalenessWindowSeconds),
        lastUpdateOf: dto => dto.LastUpdate);

    /// <summary>Internal test seam: builds a store bound to a fake clock (and
    /// optional max age) so the freshness tests can drive time.</summary>
    internal static TelemetryStore<CalendarSnapshot> CreateStoreForTest(TimeProvider timeProvider, TimeSpan? maxAge = null)
        => Facade.CreateStoreForTest(timeProvider, maxAge);

    /// <summary>Internal test seam: installs the store behind the static
    /// read/update surface.</summary>
    internal static TelemetryStore<CalendarSnapshot> StoreForTest
    {
        get => Facade.StoreForTest;
        set => Facade.StoreForTest = value;
    }

    /// <summary>Returns the cached snapshot when it is fresh enough, else null
    /// (the widget renders its unavailable state).</summary>
    public static CalendarSnapshot? TryReadFresh() => Facade.TryReadFresh();

    /// <summary>Returns the cached snapshot regardless of freshness (the
    /// inspector's feed picker may need the last-known calendar list even when
    /// stale).</summary>
    public static CalendarSnapshot ReadSnapshot() => Facade.ReadSnapshot();

    /// <summary>Stores a snapshot from the producer. The single write entry
    /// point.</summary>
    public static void UpdateFromDto(CalendarSnapshot dto) => Facade.UpdateFromDto(dto);

    /// <summary>Resets the cache to the unavailable state. Intended for test
    /// isolation.</summary>
    public static void Reset() => Facade.Reset();
}
