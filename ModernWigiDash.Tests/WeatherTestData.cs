namespace ModernWigiDash.Tests;

/// <summary>
/// The weather cluster's shared wire-format fixtures — the forecast and
/// geocoding response bodies every weather test class rides. One home instead
/// of per-class copies, so the client tests, the widget tests, and the
/// inspector-panel tests can never carry divergent payloads: a fixture
/// change edits one file, and a stale response shape fails everywhere it is
/// referenced. The widget test's New York geocode stub stays local to that
/// file (it is a single-city resolution stub, not a cluster fixture).
/// </summary>
public static class WeatherTestData
{
    /// <summary>The modern forecast response shape (current + hourly +
    /// daily, weather_code names): the client's canonical fetch fixture.</summary>
    internal const string SampleForecast = """
    {
      "latitude": 40.7128, "longitude": -74.006,
      "current": { "temperature_2m": 12.5, "relative_humidity_2m": 60, "apparent_temperature": 10.1, "weather_code": 2, "wind_speed_10m": 8.2, "time": "2026-08-07T12:00" },
      "hourly": {
        "time": ["2026-08-07T00:00", "2026-08-07T01:00"],
        "temperature_2m": [12.5, 13.1],
        "relative_humidity_2m": [40, 45],
        "weather_code": [2, 2]
      },
      "daily": {
        "time": ["2026-08-07", "2026-08-08"],
        "weather_code": [2, 3],
        "temperature_2m_max": [18.0, 20.0],
        "temperature_2m_min": [9.0, 11.0]
      }
    }
    """;

    /// <summary>The legacy forecast response shape (current_weather +
    /// relativehumidity_2m + weathercode) must still parse — stale caches and
    /// edge responses carry it. The widget and inspector tests deliberately
    /// ride THIS legacy shape as their forecast fixture, so every class shares
    /// the one copy.</summary>
    internal const string SampleForecastLegacy = """
    {
      "latitude": 40.7128, "longitude": -74.006,
      "current_weather": { "temperature": 12.5, "windspeed": 8.2, "weathercode": 2, "time": "2026-08-07T12:00" },
      "hourly": {
        "time": ["2026-08-07T12:00", "2026-08-07T13:00"],
        "temperature_2m": [12.5, 13.1],
        "relativehumidity_2m": [60, 58],
        "weathercode": [2, 2]
      },
      "daily": {
        "time": ["2026-08-07", "2026-08-08"],
        "weathercode": [2, 3],
        "temperature_2m_max": [18.0, 20.0],
        "temperature_2m_min": [9.0, 11.0]
      }
    }
    """;

    /// <summary>A single unambiguous city geocode result (Berlin).</summary>
    internal const string SampleGeocode = """
    {
      "results": [ { "name": "Berlin", "latitude": 52.52, "longitude": 13.405, "country": "Germany" } ]
    }
    """;

    /// <summary>Two same-named cities across countries: the exact-name match
    /// must beat the higher-population fuzzy match (the Vitoria/Victoria bug).</summary>
    internal const string SampleSameNameMultiCountry = """
    {
      "results": [
        { "name": "Victoria", "latitude": 48.4284, "longitude": -123.3656, "admin1": "British Columbia", "country": "Canada", "country_code": "CA", "population": 335696 },
        { "name": "Vit\u00f3ria", "latitude": -20.3194, "longitude": -40.3378, "admin1": "Esp\u00edrito Santo", "country": "Brazil", "country_code": "BR", "population": 1962476 }
      ]
    }
    """;

    /// <summary>Two same-named cities in one country: the state suffix must
    /// pick the right admin1 even when the wrong one is listed first with
    /// more people.</summary>
    internal const string SampleSpringfields = """
    {
      "results": [
        { "name": "Springfield", "latitude": 37.21533, "longitude": -93.29824, "admin1": "Missouri", "country": "United States", "country_code": "US", "population": 167601 },
        { "name": "Springfield", "latitude": 42.10148, "longitude": -72.58981, "admin1": "Massachusetts", "country": "United States", "country_code": "US", "population": 155932 }
      ]
    }
    """;

    /// <summary>Identical names across countries: the CountryCode hint must decide.</summary>
    internal const string SampleSanJoses = """
    {
      "results": [
        { "name": "San Jose", "latitude": 37.33939, "longitude": -121.89496, "admin1": "California", "country": "United States", "country_code": "US", "population": 1026908 },
        { "name": "San Jose", "latitude": 9.92807, "longitude": -84.09072, "admin1": "San Jos\u00e9 Province", "country": "Costa Rica", "country_code": "CR", "population": 335007 }

      ]
    }
    """;

    /// <summary>The real Open-Meteo candidate set for a bare "Berlin"
    /// (captured from the live API): FOUR places share the exact name (DE,
    /// NH, NJ, WI) plus one Brunswick decoy — the bare-name tie returns null
    /// (no fetch) instead of a population-decided pick, so the reported
    /// on-device symptom (a US Berlin user seeing Berlin DE's weather) cannot
    /// recur. The suffix and country-hint tests pin the escape routes out of
    /// the tie.</summary>
    internal const string SampleBerlines = """
    {
      "results": [
        { "name": "Berlin", "admin1": "State of Berlin", "country": "Germany", "country_code": "DE", "population": 3426354, "latitude": 52.52437, "longitude": 13.41053 },
        { "name": "Berlin", "admin1": "New Hampshire", "country": "United States", "country_code": "US", "population": 9367, "latitude": 44.46867, "longitude": -71.18508 },
        { "name": "Berlin", "admin1": "New Jersey", "country": "United States", "country_code": "US", "population": 7590, "latitude": 39.79123, "longitude": -74.92905 },
        { "name": "Brunswick", "admin1": "Maryland", "country": "United States", "country_code": "US", "population": 6116, "latitude": 39.31427, "longitude": -77.62777 },
        { "name": "Berlin", "admin1": "Wisconsin", "country": "United States", "country_code": "US", "population": 5420, "latitude": 43.96804, "longitude": -88.94345 }
      ]
    }
    """;

    /// <summary>The real zippopotam shape: the place (with string
    /// coordinates) lives under "places[0]" — the fixture mirrors the live
    /// API, not a hand-made root-level shape (the earlier root-level numeric
    /// fixture let the parser drift from the API, so real ZIPs silently fell
    /// back).</summary>
    internal const string SampleZip = """
    {
      "country": "United States",
      "post code": "10001",
      "places": [
        {
          "place name": "New York City",
          "longitude": "-73.9962",
          "latitude": "40.7505",
          "state": "New York"
        }
      ]
    }
    """;

    /// <summary>The zippopotam GB short-form shape (live shape: the 3-char
    /// outward code "M11" indexes; the full "M1 1AA" 404s).</summary>
    internal const string SampleZipGbM11 = """
    {
      "country": "United Kingdom",
      "post code": "M11",
      "places": [
        {
          "place name": "Manchester",
          "longitude": "-2.2374",
          "latitude": "53.4809",
          "state": "England"
        }
      ]
    }
    """;

    /// <summary>A single postal-search hit — the place whose postcode index
    /// carries the code (the geocoder's postal search returns city entries,
    /// not a place named after the code).</summary>
    internal const string SamplePostalSingleTown = """
    {
      "results": [
        { "name": "Addison", "admin1": "Texas", "country": "United States", "country_code": "US", "population": 15518, "latitude": 32.96593, "longitude": -96.88227 }
      ]
    }
    """;

    /// <summary>The live shape of a cross-country postal-code collision
    /// (75001: Paris 01, FR and Addison, TX both carry it): the tie must be
    /// gated — its candidates are the pick list, and the postal query keeps
    /// EVERY candidate (there is no exact-name row).</summary>
    internal const string SamplePostalTie = """
    {
      "results": [
        { "name": "Paris", "admin1": "Ile-de-France Region", "country": "France", "country_code": "FR", "population": 2138551, "latitude": 48.85341, "longitude": 2.3488 },
        { "name": "Addison", "admin1": "Texas", "country": "United States", "country_code": "US", "population": 15518, "latitude": 32.96593, "longitude": -96.88227 },
        { "name": "Paris 01 Louvre", "admin1": "Ile-de-France Region", "country": "France", "country_code": "FR", "population": 15114, "latitude": 48.8592, "longitude": 2.3412 }
      ]
    }
    """;

    /// <summary>The same-country population-tiebreak shape (the Accra case): two
    /// exact-name candidates in one country where the country-name suffix
    /// matches both — the ranking resolves the larger population, but BOTH
    /// enter the pick list, so a warm pick of the smaller one must expose its
    /// own population (the pick path's one channel).</summary>
    internal const string SampleGhanaAccras = """
    {
      "results": [
        { "name": "Accra", "admin1": "Greater Accra", "country": "Ghana", "country_code": "GH", "population": 100000, "latitude": 5.60372, "longitude": -0.18699 },
        { "name": "Accra", "admin1": "Accra West", "country": "Ghana", "country_code": "GH", "population": 200000, "latitude": 5.65000, "longitude": -0.15000 }
      ]
    }
    """;

    /// <summary>The live "Springfield" search shape with its fuzzy rows —
    /// the geocoder returns "Palmyra" and "Jackson" inside a "Springfield"
    /// result set; the fuzzy rows must never enter the pick list.</summary>
    internal const string SampleSpringfieldsWithFuzzy = """
    {
      "results": [
        { "name": "Springfield", "latitude": 42.10148, "longitude": -72.58981, "admin1": "Massachusetts", "country": "United States", "country_code": "US", "population": 154341 },
        { "name": "Palmyra", "latitude": 39.79421, "longitude": -91.52321, "admin1": "Missouri", "country": "United States", "country_code": "US", "population": 3616 },
        { "name": "Springfield", "latitude": 37.21533, "longitude": -93.29824, "admin1": "Missouri", "country": "United States", "country_code": "US", "population": 170188 }
      ]
    }
    """;
}
