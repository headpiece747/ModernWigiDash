namespace ModernWigiDash.Widgets;

/// <summary>
/// The outcome of one <see cref="WeatherClient.FetchCurrentAsync"/> call. The
/// union shapes make "a result with no snapshot" unrepresentable: the snapshot
/// exists only on <see cref="Fetched"/>.
/// </summary>
internal abstract record WeatherFetchResult
{
    /// <summary>A fresh snapshot was produced and the fetch completed; the
    /// caller applies it (and may write back the resolved label). Fresh means
    /// the resolution identity still matched at the completion of the WHOLE
    /// capture window — through the cache write — so an invalidation landing
    /// in that window converts the outcome to <see cref="Stale"/> instead.
    /// Carries the resolved identity the snapshot was fetched for: the geocode
    /// candidates (the widget's "Location Match" dropdown), the winner's
    /// population, and the resolution query key the fetch started for (the
    /// ADR-0006 carried key — the identity the outcome was resolved for,
    /// pinned at the client's boundary). The resolved display name rides the
    /// snapshot itself (one label source).</summary>
    public sealed record Fetched(
        WeatherSnapshot Snapshot,
        IReadOnlyList<GeocodeCandidate> Candidates,
        double Population,
        string QueryKey) : WeatherFetchResult;

    /// <summary>A genuine same-name tie: the geocoder found multiple places
    /// bearing the query's name and the rules refuse to guess coordinates. No
    /// snapshot exists, but the tied candidates are carried (they are the
    /// widget's "Location Match" dropdown) with the resolution query key (the
    /// identity the tied candidates belong to), so the caller can OFFER the
    /// user a pick instead of collapsing the outcome
    /// into a bare <see cref="Failed"/> dead end. A pick then rides the normal pick
    /// path (Location Match → the geocoder's zero-HTTP fast path). The
    /// distinction matters: a <see cref="Failed"/> for a typo'd city has no
    /// candidates to offer, but a tie does — the dropdown is the escape hatch.</summary>
    public sealed record Tie(
        IReadOnlyList<GeocodeCandidate> Candidates,
        string QueryKey) : WeatherFetchResult;

    /// <summary>The throttle window was open; no request was made and the
    /// caller keeps its previous state.</summary>
    public sealed record Throttled : WeatherFetchResult;

    /// <summary>Another fetch was already in flight; no request was made and
    /// the caller keeps its previous state (the in-flight fetch applies when
    /// it completes).</summary>
    public sealed record InFlight : WeatherFetchResult;

    /// <summary>The request failed or the location could not be resolved; no
    /// snapshot and the caller keeps its previous state.</summary>
    public sealed record Failed : WeatherFetchResult;

    /// <summary>The resolution identity was invalidated while the fetch was in
    /// flight; the snapshot is stale and must not be applied, and no throttle
    /// stamp was written (the caller re-fetches the new identity immediately).
    /// Carries the resolution query key the fetch started for (the identity
    /// that went stale, pinned at the client's boundary).</summary>
    public sealed record Stale(string QueryKey) : WeatherFetchResult;
}
