namespace ModernWigiDash.Widgets;

/// <summary>
/// One owner of the weather cluster's resolution identity (ADR-0006):
/// the query key naming "which place was this weather resolved for".
/// The client's capture window, the cache-load identity stamp, and the
/// widget's return-to-apply recheck all route through <see cref="Build"/>
/// and <see cref="SameKey"/> — one spelling of the identity, one ordinal
/// predicate, no second rule held in comments.
/// </summary>
internal static class WeatherQueryKey
{
    /// <summary>The one field whose change invalidates the resolved
    /// name/population through its own branch (the widget's
    /// InvalidateCoordinates) instead of the full re-fetch set — while
    /// still turning the key.</summary>
    internal const string LocationMatchProperty = nameof(WeatherLocation.LocationMatch);

    /// <summary>The six identity fields in key order: a change in any one
    /// is an identity change (a re-fetch). WeatherLocation.CustomLabel is
    /// deliberately absent — a label edit must not re-fetch.</summary>
    internal static readonly string[] KeyPropertyNames =
    [
        nameof(WeatherLocation.LocationType),
        nameof(WeatherLocation.Location),
        nameof(WeatherLocation.Latitude),
        nameof(WeatherLocation.Longitude),
        nameof(WeatherLocation.CountryCode),
        LocationMatchProperty,
    ];

    /// <summary>The five resolution inputs that force a re-fetch on change
    /// — every key field except <see cref="LocationMatchProperty"/>, which
    /// has its own invalidation branch.
    /// <see cref="WeatherResolvedIdentity.ResolutionInvalidationProperties"/>
    /// aliases this set, so the widget's drift test pins it to the record.</summary>
    internal static readonly string[] InvalidationProperties =
    [
        nameof(WeatherLocation.Location),
        nameof(WeatherLocation.Latitude),
        nameof(WeatherLocation.Longitude),
        nameof(WeatherLocation.CountryCode),
        nameof(WeatherLocation.LocationType),
    ];

    /// <summary>
    /// The resolution identity key — one spelling for the client's per-query
    /// geocode cache, the cache-load identity stamp, and the widget's
    /// in-flight staleness guard: a change in any resolution input yields a
    /// different key. Fields are backslash-escaped and joined with '|' so a
    /// separator character inside a field can never forge a colliding key.
    /// </summary>
    internal static string Build(WeatherLocation location)
        => string.Join('|',
            EscapeKeyField(location.LocationType), EscapeKeyField(location.Location),
            EscapeKeyField(location.Latitude), EscapeKeyField(location.Longitude),
            EscapeKeyField(location.CountryCode), EscapeKeyField(location.LocationMatch));

    /// <summary>The single identity predicate: ordinal comparison — case is
    /// identity, so a case change is a new place. Null-safe on both sides.</summary>
    internal static bool SameKey(string? left, string? right)
        => string.Equals(left, right, StringComparison.Ordinal);

    private static string EscapeKeyField(string? value)
        => (value ?? "").Replace("\\", "\\\\").Replace("|", "\\|");
}
