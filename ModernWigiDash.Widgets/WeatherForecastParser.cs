using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The Open-Meteo response parser: the pure JSON → forecast-domain-data
/// mapping for the current, hourly, and daily sections — the modern-vs-legacy
/// shape handling, the humidity's current-block provenance, the day-name
/// formatting, and the fetch-limit capping. The client keeps the snapshot
/// assembly (and the identity/stale semantics); this module owns the parsing
/// rules, so a response-shape change edits one file and the rules are
/// testable without a fetch.
/// </summary>
internal static class WeatherForecastParser
{
    /// <summary>
    /// Parses current conditions. The modern <c>current</c> block (15-minute
    /// precision, humidity + feels-like included) is primary; the legacy
    /// <c>current_weather</c> block is the fallback so cached/legacy responses
    /// still parse. The legacy block never carried apparent_temperature or
    /// humidity, which is why this reads them from <c>current</c>.
    /// </summary>
    internal static (double? TempC, double? FeelsLikeC, double? WindSpeedKmH, int? WeatherCode, bool? IsDay) ParseCurrentWeather(JsonElement root)
    {
        double? tempC = null;
        double? feelsLikeC = null;
        double? windSpeedKmH = null;
        int? weatherCode = null;
        bool? isDay = null;

        if (root.TryGetProperty("current", out var current))
        {
            if (current.TryGetProperty("temperature_2m", out var tempEl))
                tempC = tempEl.GetDouble();
            if (current.TryGetProperty("apparent_temperature", out var feelsEl))
                feelsLikeC = feelsEl.GetDouble();
            if (current.TryGetProperty("wind_speed_10m", out var windEl))
                windSpeedKmH = windEl.GetDouble();
            if (current.TryGetProperty("weather_code", out var codeEl))
                weatherCode = codeEl.GetInt32();
            // is_day is the API's day/night fact at the location: the
            // current-condition icon flips to a moon at night. Absent is
            // unknown (null) — the display keeps its previous value.
            if (current.TryGetProperty("is_day", out var dayEl))
                isDay = dayEl.GetInt32() == 1;
            return (tempC, feelsLikeC, windSpeedKmH, weatherCode, isDay);
        }

        if (!root.TryGetProperty("current_weather", out var currentWeather)) return (null, null, null, null, null);

        if (currentWeather.TryGetProperty("temperature", out var legacyTemp))
            tempC = legacyTemp.GetDouble();
        // The legacy block has no apparent_temperature — the URL's
        // apparent_temperature=true hint never materialized there, so the
        // "feels like" metric can only come from the modern current block.
        if (currentWeather.TryGetProperty("apparent_temperature", out var legacyFeels))
            feelsLikeC = legacyFeels.GetDouble();
        if (currentWeather.TryGetProperty("windspeed", out var legacyWind))
            windSpeedKmH = legacyWind.GetDouble();
        if (currentWeather.TryGetProperty("weathercode", out var legacyCode))
            weatherCode = legacyCode.GetInt32();
        if (currentWeather.TryGetProperty("is_day", out var legacyDay))
            isDay = legacyDay.GetInt32() == 1;

        return (tempC, feelsLikeC, windSpeedKmH, weatherCode, isDay);
    }

    /// <summary>
    /// Parses the humidity (a current condition) and the hourly strip.
    /// </summary>
    internal static (double? Humidity, IReadOnlyList<HourlyForecastItem>? Hourly) ParseHourlyForecast(JsonElement root)
    {
        // Humidity is a current condition — the modern current block carries
        // it at 15-minute precision. The hourly array starts at local midnight,
        // so its first bucket is hours stale; never use it as "current".
        if (root.TryGetProperty("current", out var current)
            && current.TryGetProperty("relative_humidity_2m", out var humEl))
        {
            double? humidity = humEl.GetDouble();

            IReadOnlyList<HourlyForecastItem>? hourlyForecasts = ParseHourlyItems(root);
            return (humidity, hourlyForecasts);
        }

        if (!root.TryGetProperty("hourly", out var hourly)
            || !hourly.TryGetProperty("temperature_2m", out var temps)
            || temps.GetArrayLength() <= 0) return (null, null);

        // Legacy fallback: no current block, so the hourly array is the only
        // humidity source (still stale-by-hours — better than nothing).
        double? legacyHumidity = null;
        if (hourly.TryGetProperty("relativehumidity_2m", out var hums) && hums.GetArrayLength() > 0)
            legacyHumidity = hums[0].GetDouble();

        return (legacyHumidity, ParseHourlyItems(root));
    }

    private static IReadOnlyList<HourlyForecastItem>? ParseHourlyItems(JsonElement root)
    {
        if (!root.TryGetProperty("hourly", out var hourly)) return null;
        if (!hourly.TryGetProperty("time", out var times)
            || !hourly.TryGetProperty("temperature_2m", out var tempsInner)
            || times.GetArrayLength() <= 0 || tempsInner.GetArrayLength() <= 0) return null;

        // Both the modern (weather_code) and legacy (weathercode) names are
        // honored so either response shape parses.
        bool modernCodes = hourly.TryGetProperty("weather_code", out var codes);
        bool legacyCodes = !modernCodes && hourly.TryGetProperty("weathercode", out codes);

        // Ragged remote arrays degrade per column: the loop bound must cover
        // EVERY indexed array — a shorter weather_code caps the strip, it
        // does not throw out of bounds (one degraded column, not the fetch).
        int hLen = Math.Min(times.GetArrayLength(), tempsInner.GetArrayLength());
        if (modernCodes || legacyCodes) hLen = Math.Min(hLen, codes.GetArrayLength());
        List<HourlyForecastItem> items = [];
        for (int i = 0; i < Math.Min(hLen, WeatherForecastLimits.MaxFetchHours); i++)
        {
            string timeStr = times[i].GetString() ?? "";
            string label = timeStr.Length >= 16 ? timeStr[11..16] : $"{i}:00";
            int code = modernCodes || legacyCodes ? codes[i].GetInt32() : 0;
            items.Add(new HourlyForecastItem(label, tempsInner[i].GetDouble(), code));
        }
        return items;
    }

    /// <summary>
    /// Parses the daily high/low and the daily strip.
    /// </summary>
    internal static (double? HighTempC, double? LowTempC, IReadOnlyList<DailyForecastItem>? Daily) ParseDailyForecast(JsonElement root)
    {
        if (!root.TryGetProperty("daily", out var daily)) return (null, null, null);

        double? highTempC = null;
        double? lowTempC = null;
        if (daily.TryGetProperty("temperature_2m_max", out var maxes) && maxes.GetArrayLength() > 0)
            highTempC = maxes[0].GetDouble();
        if (daily.TryGetProperty("temperature_2m_min", out var mins) && mins.GetArrayLength() > 0)
            lowTempC = mins[0].GetDouble();

        IReadOnlyList<DailyForecastItem>? dailyForecasts = null;
        if (daily.TryGetProperty("time", out var dTimes) && daily.TryGetProperty("temperature_2m_max", out maxes))
        {
            // Both the modern (weather_code) and legacy (weathercode) names are
            // honored so either response shape parses.
            bool modernCodes = daily.TryGetProperty("weather_code", out var dCodes);
            bool legacyCodes = !modernCodes && daily.TryGetProperty("weathercode", out dCodes);
            if (modernCodes || legacyCodes)
            {
                dailyForecasts = BuildDailyItems(dTimes, maxes, mins, dCodes);
            }
        }
        return (highTempC, lowTempC, dailyForecasts);
    }

    private static List<DailyForecastItem> BuildDailyItems(JsonElement dTimes, JsonElement maxes, JsonElement mins, JsonElement codes)
    {
        // The same per-column posture as the hourly strip: the bound covers
        // every indexed array, so a ragged min/code column degrades the strip.
        // A MISSING min column reads as a non-array token (the scalar read
        // above keeps the "omitted section" rule) — length 0, not a throw:
        // one absent column degrades the strip to empty, it does not fail
        // the fetch.
        int dLen = Math.Min(dTimes.GetArrayLength(), maxes.GetArrayLength());
        dLen = Math.Min(dLen, mins.ValueKind == JsonValueKind.Array ? mins.GetArrayLength() : 0);
        dLen = Math.Min(dLen, codes.GetArrayLength());
        List<DailyForecastItem> items = [];
        for (int i = 0; i < Math.Min(dLen, WeatherForecastLimits.MaxFetchDays); i++)
        {
            string dateStr = dTimes[i].GetString() ?? "";
            // Day names render in the INVARIANT calendar (the strip's "Today"
            // marker is English by design) — DayOfWeek.ToString() would mix
            // "Today" with the OS locale's weekday on non-English systems.
            string dayName = DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate.ToString("dddd", CultureInfo.InvariantCulture)
                : $"Day {i + 1}";
            items.Add(new DailyForecastItem(
                i == 0 ? "Today" : dayName,
                maxes[i].GetDouble(),
                mins[i].GetDouble(),
                codes[i].GetInt32()));
        }
        return items;
    }
}
