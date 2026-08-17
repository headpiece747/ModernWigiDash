namespace ModernWigiDash.Widgets;

/// <summary>
/// One complete Open-Meteo fetch result. Fields are null when the response
/// omitted that section — consumers keep their previous value in that case.
/// </summary>
internal sealed record WeatherSnapshot(
    double? CurrentTempC,
    double? FeelsLikeC,
    double? Humidity,
    double? WindSpeedKmH,
    int? WeatherCode,
    double? HighTempC,
    double? LowTempC,
    IReadOnlyList<DailyForecastItem>? DailyForecasts,
    IReadOnlyList<HourlyForecastItem>? HourlyForecasts,
    string ResolvedCityName,
    double Lat,
    double Lon);
