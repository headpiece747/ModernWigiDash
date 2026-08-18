namespace ModernWigiDash.Widgets;

/// <summary>
/// The resolved-identity invalidation rule: which property edit drops which
/// granularity of resolved state, spelled ONCE so the client's fetch-control
/// twin and the widget's resolved-identity twin can never drift. Every
/// resolution-input edit routes through <see cref="KindForProperty"/>; each
/// twin keeps its own field set (the state differs — the client owns the
/// throttle and the identity query, the widget owns the pending label
/// write-back) but both implement the SAME declared
/// <see cref="WeatherInvalidationKind"/>. The twin-equivalence pin
/// (WeatherInvalidationTests) drives both twins through every kind, so a new
/// resolved field or pending state added to one twin is caught the moment the
/// other drifts.
/// </summary>
internal static class WeatherInvalidation
{
    /// <summary>
    /// The one property → drop-granularity map: the Location Match pick drops
    /// coordinates only (its candidates stay — a pick resolves against the
    /// candidates it was offered from), every other resolution input drops the
    /// whole identity (a pick made against a changed input must never win).
    /// Whether a suppressed write-back edit triggers any drop at all is the
    /// caller's flow concern (the suppression flag lives with the write-back),
    /// not this rule's — the rule only says which kind an edit IS.
    /// </summary>
    internal static WeatherInvalidationKind KindForProperty(string propertyName)
    {
        if (string.Equals(propertyName, WeatherQueryKey.LocationMatchProperty, StringComparison.Ordinal))
        {
            return WeatherInvalidationKind.Coordinates;
        }

        return WeatherQueryKey.InvalidationProperties.Contains(propertyName)
            ? WeatherInvalidationKind.Location
            : WeatherInvalidationKind.None;
    }
}

/// <summary>
/// The drop granularity an edit triggers — the semantics of what each twin's
/// drop operation clears and keeps. Spelled here once, implemented by both
/// twins, pinned by the twin-equivalence test. A new resolved field or
/// pending state must be added to both twins' drop paths; the pin is what
/// catches a twin that forgets it.
/// </summary>
internal enum WeatherInvalidationKind
{
    /// <summary>No resolved state drops (the edit is not a resolution input —
    /// e.g. the custom label, which is deliberately identity-absent).</summary>
    None,

    /// <summary>
    /// The Location Match pick: the resolved coordinates, name, and population
    /// drop (plus the widget twin's pending label write-back), and the client
    /// twin's identity query + throttle reset so the pick re-resolves
    /// immediately. The geocode candidates survive on BOTH twins — a pick
    /// resolves against the candidates it was offered from.
    /// </summary>
    Coordinates,

    /// <summary>
    /// Every other resolution input: the WHOLE resolved identity is voided —
    /// coordinates, name, population, and the geocode candidates (plus the
    /// widget twin's pending label write-back) — and the client twin's
    /// identity query + throttle reset so the new input re-fetches
    /// immediately.
    /// </summary>
    Location,
}