namespace ModernWigiDash.Widgets;

/// <summary>The metric-pill visibility flags and values the Detailed mode needs.</summary>
internal sealed record WeatherMetricsInput(
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
/// The data-side inputs for <see cref="WeatherPresentation.Build"/>: the
/// current conditions, the unit/visibility choices, and the forecast lists
/// the Daily/Hourly strings derive from.
/// </summary>
internal sealed record WeatherDisplayInput(
    double CurrentTempC,
    WeatherMetricsInput Metrics,
    IReadOnlyList<DailyForecastItem> DailyForecasts,
    IReadOnlyList<HourlyForecastItem> HourlyForecasts);

/// <summary>
/// Everything the widget draws that is a *fact* of the weather data and
/// units: the hero temperature, the metric pills, the daily strip strings,
/// and the hourly temps — composed by <see cref="WeatherPresentation.Build"/>
/// and rendered by <see cref="WeatherWidgetRenderer"/>. The render methods
/// are thin adapters that lay these out; the display rules are assertable
/// without pixels.
/// </summary>
internal sealed record WeatherDisplay(
    string MainTemp,
    IReadOnlyList<string> Metrics,
    IReadOnlyList<string> ForecastRanges,
    IReadOnlyList<string> DailyHighLows,
    IReadOnlyList<string> HourlyTemps);

/// <summary>
/// Pure display rules for the Weather widget: the unit conversions, the WMO
/// condition table, and the composed row/pill strings the five layout modes
/// draw. Moved out of the widget's render paths and out of the data module
/// (WeatherClient), so every display string is assertable without pixels.
/// </summary>
internal static class WeatherPresentation
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

    /// <summary>The current-condition display fact for a code at a given
    /// day/night: the icon (clear skies read as a moon after dark, partly
    /// cloudy as the night city — every code without a night override keeps
    /// its day icon, so precipitation renders the same all day) and the
    /// DAY-NEUTRAL WMO description (the night flip never rewrites it). The
    /// hero paths read both from this ONE call, so the WMO table is walked
    /// once per draw, not twice.</summary>
    public static (string Icon, string Description) MapWmoIcon(int code, bool isDay)
    {
        var (dayIcon, description) = MapWmoCode(code);
        string icon = isDay
            ? dayIcon
            : code switch
            {
                0 or 1 => "🌙",
                2 => "🌃",
                _ => dayIcon,
            };
        return (icon, description);
    }

    /// <summary>The default unit-system choice — the single source for the
    /// widget's property default and the tap-toggle.</summary>
    public const string DefaultUnitSystem = "Fahrenheit (°F, mph)";

    /// <summary>The tap-toggle rule: Fahrenheit ⇄ Celsius (km/h) — the
    /// widget's badge cycles between the two primary systems.</summary>
    public static string ToggleUnitSystem(string current)
        => current.StartsWith("Fahrenheit", StringComparison.OrdinalIgnoreCase)
            ? "Celsius (°C, km/h)"
            : DefaultUnitSystem;

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

    /// <summary>Formats a Celsius temperature in the requested unit for display.
    /// The display is locale-independent: comma-decimal cultures must not
    /// render "21,5°C".</summary>
    public static string FormatTemp(double tempC, string tempUnit, bool shortFormat = false)
    {
        return tempUnit switch
        {
            "°F" => shortFormat ? $"{DisplayFormat.Value(tempC * 9.0 / 5.0 + 32.0, "F0")}°" : $"{DisplayFormat.Value(tempC * 9.0 / 5.0 + 32.0, "F0")}°F",
            "K" => $"{DisplayFormat.Value(tempC + 273.15, "F0")} K",
            _ => shortFormat ? $"{DisplayFormat.Value(tempC, "F0")}°" : $"{DisplayFormat.Value(tempC, "F1")}°C",
        };
    }

    /// <summary>Formats a km/h wind speed in the requested unit for display —
    /// locale-independent like <see cref="FormatTemp"/>.</summary>
    public static string FormatSpeed(double kmh, string speedUnit)
    {
        return speedUnit switch
        {
            "mph" => $"{DisplayFormat.Value(kmh * 0.621371, "F0")} mph",
            "m/s" => $"{DisplayFormat.Value(kmh / 3.6, "F0")} m/s",
            _ => $"{DisplayFormat.Value(kmh, "F0")} km/h",
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
            metrics.Add($"Humidity: {DisplayFormat.Pct(input.Humidity)}");
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

    /// <summary>
    /// Composes the display facts for one weather data state: the hero
    /// temperature, the metric pills, the daily strip strings (capped at
    /// <see cref="WeatherForecastLimits.MaxStripDays"/> days, the strip's draw
    /// limit), and the hourly temps (capped at
    /// <see cref="WeatherForecastLimits.MaxStripHours"/> columns, the row's
    /// draw limit). The widget measures and truncates the font-dependent
    /// pieces (header, pill widths) around this record; the draw paths never
    /// re-derive a display string from raw data.
    /// </summary>
    public static WeatherDisplay Build(WeatherDisplayInput input)
    {
        string mainTemp = FormatTemp(input.CurrentTempC, input.Metrics.TempUnit);
        IReadOnlyList<string> metrics = MetricPills(input.Metrics);

        int dayCount = Math.Min(input.DailyForecasts.Count, WeatherForecastLimits.MaxStripDays);
        var ranges = new string[dayCount];
        var highLows = new string[dayCount];
        for (int i = 0; i < dayCount; i++)
        {
            ranges[i] = ForecastRangeText(input.DailyForecasts[i].MaxTempC, input.DailyForecasts[i].MinTempC, input.Metrics.TempUnit);
            highLows[i] = DailyHighLowText(input.DailyForecasts[i].MaxTempC, input.DailyForecasts[i].MinTempC, input.Metrics.TempUnit);
        }

        int hourCount = Math.Min(input.HourlyForecasts.Count, WeatherForecastLimits.MaxStripHours);
        var temps = new string[hourCount];
        for (int i = 0; i < hourCount; i++)
        {
            temps[i] = FormatTemp(input.HourlyForecasts[i].TempC, input.Metrics.TempUnit);
        }

        return new WeatherDisplay(mainTemp, metrics, ranges, highLows, temps);
    }

    /// <summary>The neutral resolved-name label when no resolution exists —
    /// the display's unknown-location fact: the header shows it until a
    /// resolution, and the subtitle's unresolved verdict recognizes it.</summary>
    internal const string UnknownLocationLabel = "Unknown location";

    /// <summary>
    /// The subtitle guidance line below the header: the ONE spelling of the
    /// guidance text. The priority order ensures the most actionable message
    /// wins — a tie always beats "set a location". A custom label adds no
    /// confirmation line: echoing the resolved city under the label was the
    /// "still shows underneath" complaint, so a resolved city draws no
    /// subtitle at all. The unresolved verdict (blank or the neutral
    /// unknown-location label) is a display fact owned HERE, so the build
    /// module and the widget no longer reach into fetch-control state for
    /// it. Null: no guidance applies.
    /// </summary>
    public static string? BuildSubtitle(string? resolvedCity, string? locationText, int candidateCount, int dailyCount)
    {
        bool isUnresolved = string.IsNullOrWhiteSpace(resolvedCity)
            || string.Equals(resolvedCity, UnknownLocationLabel, StringComparison.Ordinal);
        if (candidateCount > 0 && dailyCount == 0)
        {
            // Ambiguous tie: candidates exist but no weather data — the
            // Location Match dropdown is the documented escape route.
            return "Multiple cities found \u2014 pick one in Settings";
        }
        if (isUnresolved && string.IsNullOrWhiteSpace(locationText))
        {
            // No location set yet.
            return "Set a location in Settings";
        }
        if (isUnresolved)
        {
            // Location was set but failed to resolve.
            return "Check spelling \u2014 try 'City, State' or 'City, Country'";
        }
        return null;
    }

    /// <summary>The header staleness line: "Updating…" while a fetch is in
    /// flight, the time-ago of the last successful fetch otherwise, null when
    /// there is nothing to report (no fetch ever succeeded).</summary>
    public static string? BuildStalenessText(bool isFetching, DateTime lastSuccessFetchTime, DateTime now)
    {
        if (isFetching) return "Updating\u2026";
        if (lastSuccessFetchTime <= DateTime.MinValue) return null;
        return FormatTimeAgo(now - lastSuccessFetchTime);
    }

    /// <summary>
    /// Formats a time-ago span into a human-readable staleness string.
    /// Pinned by the weather widget's staleness tests.
    /// </summary>
    public static string FormatTimeAgo(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes < 1) return "Updated just now";
        if (elapsed.TotalMinutes < 60) return $"Updated {(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"Updated {(int)elapsed.TotalHours}h ago";
        return $"Updated {(int)elapsed.TotalDays}d ago";
    }
}
