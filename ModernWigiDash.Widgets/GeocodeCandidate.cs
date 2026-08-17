namespace ModernWigiDash.Widgets;

/// <summary>
/// One geocoding candidate the user can pick from the widget's "Location
/// Match" dropdown: the display label (Name, Admin1, Country) and the exact
/// coordinates it resolves to. When the user picks one, the widget re-fetches
/// with its query so the pick is honored deterministically.
/// </summary>
public sealed record GeocodeCandidate(string Label, string Query, double Lat, double Lon)
{
    /// <summary>Candidate population (the search list's disambiguating label
    /// data; 0 when the geocoder omitted it).</summary>
    public double Population { get; init; }
}
