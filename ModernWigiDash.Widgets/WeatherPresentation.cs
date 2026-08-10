namespace ModernWigiDash.Widgets;

/// <summary>The metric-pill visibility flags and values the Detailed mode needs.</summary>
public sealed record WeatherMetricsInput(
    bool ShowFeelsLike,
    double FeelsLikeC,
    bool ShowHumidity,
    double Humidity,
    bool ShowWind,
    double WindKmh,
    bool ShowHighLow,
    double HighC,
    double LowC,
    string TempUnit,
    string SpeedUnit);

/// <summary>
/// Pure display rules for the Weather widget: the unit conversions, the WMO
/// condition table, and the composed row/pill strings the five layout modes
/// draw. Moved out of the widget's render paths and out of the data module
/// (WeatherClient), so every display string is assertable without pixels.
/// </summary>
public static class WeatherPresentation
{
    /// <summary>WMO weather-code → (emoji icon, short description).</summary>
    public static (string Icon, string Description) MapWmoCode(int code)
    {
        return code switch
        {
            0 => ("☀️", "Clear Sky"),
            1 => ("🌤️", "Mainly Clear"),
            2 => ("⛅", "Partly Cloudy"),
            3 => ("☁️", "Overcast"),
            45 or 48 => ("🌫️", "Foggy"),
            51 or 53 or 55 => ("🌧️", "Drizzle"),
            56 or 57 => ("🌧️❄️", "Freezing Drizzle"),
            61 or 63 or 65 => ("🌧️", "Rainy"),
            66 or 67 => ("🌧️❄️", "Freezing Rain"),
            71 or 73 or 75 or 77 => ("❄️", "Snowy"),
            80 or 81 or 82 => ("🌦️", "Rain Showers"),
            85 or 86 => ("🌨️", "Snow Showers"),
            95 or 96 or 99 => ("🌩️", "Thunderstorm"),
            _ => ("☀️", "Fair")
        };
    }

    /// <summary>Parses the unit-system choice string into the display unit tokens.</summary>
    public static (string tempUnit, string speedUnit) ParseUnitSystem(string unitSystem)
    {
        return unitSystem switch
        {
            "Fahrenheit (°F, mph)" => ("°F", "mph"),
            "Celsius (°C, km/h)" or "" or null => ("°C", "km/h"),
            "Celsius (°C, mph)" => ("°C", "mph"),
            "Celsius (°C, m/s)" => ("°C", "m/s"),
            "Kelvin (K, m/s)" => ("K", "m/s"),
            _ => ("°C", "km/h"),
        };
    }

    /// <summary>Formats a Celsius temperature in the requested unit for display.</summary>
    public static string FormatTemp(double tempC, string tempUnit, bool shortFormat = false)
    {
        return tempUnit switch
        {
            "°F" => shortFormat ? $"{(tempC * 9.0 / 5.0 + 32.0):F0}°" : $"{(tempC * 9.0 / 5.0 + 32.0):F0}°F",
            "K" => $"{tempC + 273.15:F0} K",
            _ => shortFormat ? $"{tempC:F0}°" : $"{tempC:F1}°C",
        };
    }

    /// <summary>Formats a km/h wind speed in the requested unit for display.</summary>
    public static string FormatSpeed(double kmh, string speedUnit)
    {
        return speedUnit switch
        {
            "mph" => $"{(kmh * 0.621371):F0} mph",
            "m/s" => $"{(kmh / 3.6):F0} m/s",
            _ => $"{kmh:F0} km/h",
        };
    }

    /// <summary>
    /// The Detailed-mode metric pills, in the widget's fixed order. Only the
    /// enabled pills appear; the strings carry their own units.
    /// </summary>
    public static IReadOnlyList<string> MetricPills(WeatherMetricsInput input)
    {
        List<string> metrics = [];
        if (input.ShowFeelsLike)
        {
            metrics.Add($"Feels: {FormatTemp(input.FeelsLikeC, input.TempUnit, true)}");
        }
        if (input.ShowHumidity)
        {
            metrics.Add($"Humidity: {input.Humidity:F0}%");
        }
        if (input.ShowWind)
        {
            metrics.Add($"Wind: {FormatSpeed(input.WindKmh, input.SpeedUnit)}");
        }
        if (input.ShowHighLow)
        {
            metrics.Add($"H:{FormatTemp(input.HighC, input.TempUnit, true)} L:{FormatTemp(input.LowC, input.TempUnit, true)}");
        }
        return metrics;
    }

    /// <summary>The "hi / lo" range string under each forecast-strip day.</summary>
    public static string ForecastRangeText(double maxTempC, double minTempC, string tempUnit)
        => $"{FormatTemp(maxTempC, tempUnit, true)} / {FormatTemp(minTempC, tempUnit, true)}";

    /// <summary>The "High: ...  Low: ..." row in the Daily forecast mode.</summary>
    public static string DailyHighLowText(double maxTempC, double minTempC, string tempUnit)
        => $"High: {FormatTemp(maxTempC, tempUnit)}  Low: {FormatTemp(minTempC, tempUnit)}";
}
