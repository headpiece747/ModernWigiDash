namespace ModernWigiDash.Widgets;

/// <summary>
/// The outcome of one <see cref="WeatherClient.FetchCurrentAsync"/> call. The
/// union shapes make "a result with no snapshot" unrepresentable: the snapshot
/// exists only on <see cref="Fetched"/>.
/// </summary>
internal abstract record WeatherFetchResult
{
    /// <summary>A fresh snapshot was produced and the fetch completed; the
    /// caller applies it (and may write back the resolved label). Carries the
    /// resolved identity the snapshot was fetched for: the geocode candidates
    /// (the widget's "Location Match" dropdown), the winner's population, and
    /// the resolution query key the fetch started for — so the widget can
    /// verify a result belongs to the identity it requested instead of
    /// re-deriving the key itself. The resolved display name rides the
    /// snapshot itself (one label source).</summary>
    public sealed record Fetched(
        WeatherSnapshot Snapshot,
        IReadOnlyList<GeocodeCandidate> Candidates,
        double Population,
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
    /// Carries the resolution query key the fetch started for, so the caller
    /// can tell WHICH identity went stale without re-deriving the key.</summary>
    public sealed record Stale(string QueryKey) : WeatherFetchResult;
}
