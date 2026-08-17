namespace ModernWigiDash.Widgets;

/// <summary>
/// A resolved place the weather is fetched for. Latitude/Longitude are the
/// optional explicit coordinate overrides; when either is empty the location
/// query (city name, ZIP, or "lat,lon" pair) is resolved via geocoding.
/// <see cref="CountryCode"/> is the optional ISO country-code hint ("US",
/// "DE", ...) that disambiguates same-named cities across countries.
/// </summary>
internal sealed record WeatherLocation(string LocationType, string Location, string? Latitude, string? Longitude, string? CustomLabel)
{
    /// <summary>Optional ISO 3166-1 alpha-2 country-code hint for geocoding.</summary>
    public string? CountryCode { get; init; }

    /// <summary>
    /// Optional user pick from the geocoder's candidates ("Location Match"
    /// dropdown): the chosen candidate's label, resolved directly to its
    /// coordinates instead of re-geocoding.
    /// </summary>
    public string? LocationMatch { get; init; }

    public WeatherLocation(string locationType, string location, string? latitude, string? longitude, string? customLabel, string? countryCode)
        : this(locationType, location, latitude, longitude, customLabel) => CountryCode = countryCode;
}
