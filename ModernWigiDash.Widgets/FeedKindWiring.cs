using System.Collections.Concurrent;

namespace ModernWigiDash.Widgets;

/// <summary>
/// One asset kind's feed wiring row (the PriceFeedManager's kind→(validation,
/// map, startup, subscribe, seed) mapping): the validation guard, the
/// ref-counted subscription map, the first-claim startup action, the WS
/// subscribe frame builder (null when the kind has no socket), and the one-shot
/// seed leg (null when the kind's cycle already serves it). The shared routines
/// own their sequences (validate → claim → start → subscribe; the seed's fetch
/// → apply), so a fourth asset kind is one table row, not a fourth copy of the
/// steps.
/// </summary>
internal sealed class FeedKindWiring
{
    /// <summary>The symbol validation guard for this kind.</summary>
    public required Func<string, bool> IsValid { get; init; }

    /// <summary>The ref-counted subscription map (key → claim count).</summary>
    public required ConcurrentDictionary<string, int> Subscriptions { get; init; }

    /// <summary>The first-claim startup action (starts the kind's feeds).</summary>
    public required Action OnFirstClaim { get; init; }

    /// <summary>The WS subscribe frame builder (null when the kind has no socket).</summary>
    public required Func<string, string>? WsSubscribeFrame { get; init; }

    /// <summary>The one-shot seed leg (null when the kind's cycle already serves it).</summary>
    public required SeedLeg? Seed { get; init; }

    /// <summary>
    /// The WS loop this row starts: created lazily on the first claim and
    /// disposed at shutdown. Null while no claim has started it.
    /// </summary>
    public FeedLoop? Loop { get; set; }
}

/// <summary>
/// The kind's one-shot seed leg: the source label the seeded record is stored
/// under, its currency symbol, and the fetch (the leg's own validation guard
/// rides along — the seed path has no subscription boundary to validate at).
/// </summary>
internal sealed record SeedLeg(string SourceLabel, string CurrencySymbol, Func<string, CancellationToken, Task<QuoteSample?>> Fetch);
