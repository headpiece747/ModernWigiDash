namespace ModernWigiDash.Widgets;

/// <summary>
/// The shared resolved-identity value: the geocode candidates, the resolved
/// header city name, and the resolved population — the ONE value both
/// invalidation twins hold. The client's fetch-control state machine and the
/// widget's resolved-identity module each add their own unique fields around
/// it (the client owns the coordinates + identity query + throttle; the
/// widget owns the pending label write-back), but the identity itself is
/// declared once here and dropped by the single rule
/// (<see cref="WeatherInvalidation.Drop"/>). The twin-equivalence pin
/// (WeatherInvalidationTests) compares this value across the two twins, so a
/// new resolved field added to the record is caught the moment a twin's
/// transition forgets to route it through the rule.
/// </summary>
internal sealed record WeatherResolutionState(
    string ResolvedName,
    double Population,
    IReadOnlyList<GeocodeCandidate> Candidates)
{
    /// <summary>The empty identity: the drop destination and the client
    /// twin's initial state (nothing resolved yet). The widget twin starts
    /// from the neutral label instead (its ctor hands it one in — the header
    /// shows the neutral label until a resolution); every drop still lands
    /// here. One static instance: a Location drop lands on THIS state, so
    /// both twins can share it (a record with a fresh empty list per call
    /// would compare unequal across drops).</summary>
    internal static readonly WeatherResolutionState Empty = new("", 0, []);

    /// <summary>The null-keeps replacement — the "response omitted this
    /// section — keep the previous value" rule shared with the snapshot
    /// apply: a null argument keeps the current field, a non-null argument
    /// replaces it (the population sentinel 0 replaces with "no data").</summary>
    public WeatherResolutionState With(
        string? resolvedName = null,
        double? population = null,
        IReadOnlyList<GeocodeCandidate>? candidates = null)
        => this with
        {
            ResolvedName = resolvedName ?? ResolvedName,
            Population = population ?? Population,
            Candidates = candidates ?? Candidates,
        };
}