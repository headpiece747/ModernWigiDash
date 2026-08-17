namespace ModernWigiDash.Widgets;

/// <summary>
/// One column of the hourly forecast strip. Shared data model between
/// <see cref="WeatherClient"/> (producer) and the rendering widgets (consumer).
/// </summary>
internal readonly record struct HourlyForecastItem(string TimeLabel, double TempC, int WeatherCode);
