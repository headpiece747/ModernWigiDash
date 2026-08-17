namespace ModernWigiDash.Widgets;

/// <summary>
/// A day-row of the daily forecast strip. Shared data model between
/// <see cref="WeatherClient"/> (producer) and the rendering widgets (consumer).
/// </summary>
internal readonly record struct DailyForecastItem(string DayName, double MaxTempC, double MinTempC, int WeatherCode);
