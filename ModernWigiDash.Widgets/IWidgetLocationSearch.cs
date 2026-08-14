namespace ModernWigiDash.Widgets;

/// <summary>
/// Optional weather-widget capability: search-as-you-type location selection.
/// The inspector renders the search editor and commits picks through this
/// contract — never by branching on the widget type. <see cref="CommitPick"/>
/// writes the picked place's label and exact coordinates through the widget's
/// SetProperty funnel (the invariant), so the persisted profile is
/// deterministic across restarts.
/// </summary>
public interface IWidgetLocationSearch
{
    /// <summary>Geocodes <paramref name="query"/> (city name or postal code)
    /// into ranked candidates with exact coordinates; empty on error.</summary>
    Task<IReadOnlyList<GeocodeCandidate>> SearchAsync(string query, CancellationToken ct);

    /// <summary>Commits a picked candidate: Location = label, Latitude/Longitude
    /// = exact coordinates, LocationMatch cleared — via SetProperty.</summary>
    void CommitPick(GeocodeCandidate candidate);
}
