using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The render-model inputs: the cache key (the value snapshot of everything
/// that can change the formatted strings), the consistent snapshot display
/// view (read under the host's gate — the data scalars, the weather code, and
/// the two copied forecast lists), and the per-frame geometry (the header
/// layout and the scale, computed ONCE in the render tick and shared with the
/// draw path — the build never re-derives the geometry it is handed).
/// </summary>
internal sealed record WeatherRenderModelInputs(
    WeatherRenderModelKey Key,
    int WeatherCode,
    double CurrentTempC,
    double FeelsLikeC,
    double Humidity,
    double WindSpeedKmH,
    double HighTempC,
    double LowTempC,
    IReadOnlyList<DailyForecastItem> Daily,
    IReadOnlyList<HourlyForecastItem> Hourly,
    WeatherHeaderLayout Header,
    float Scale,
    string LocationText,
    int CandidateCount);

/// <summary>
/// The render-model build module: the ONE place the weather render model is
/// composed. Given the cached model and the frame's inputs, it returns the
/// cached model on a key hit, and otherwise derives the next model — the
/// display facts (hero temp, pills, daily/hourly strings) through
/// <see cref="WeatherPresentation"/>, the auto-truncated header at the same
/// max width the draw path uses, and the pill widths through the renderer's
/// shared helper. The per-frame allocation discipline lives here with the
/// cache it protects: the hit path allocates nothing, and the miss path does
/// the display composition, the header truncation, and the pill measurement
/// exactly once per rebuild.
/// </summary>
internal static class WeatherRenderModelFactory
{
    /// <summary>
    /// Returns the cached model when the inputs' key matches the cache's,
    /// otherwise composes the next model from the inputs.
    /// </summary>
    public static WeatherRenderModel Resolve(WeatherRenderModel? cached, WeatherRenderModelInputs inputs)
    {
        if (cached is { } hit && hit.Key == inputs.Key) return hit;

        // The unit pair is derived from the key's UnitSystem — the same rule
        // the draw path uses for the badge.
        var (tempUnit, speedUnit) = WeatherPresentation.ParseUnitSystem(inputs.Key.UnitSystem);

        // The display facts (hero temp, pills, daily/hourly strings) compose
        // in WeatherPresentation; the model caches them alongside the data
        // slices the draw paths need.
        var display = WeatherPresentation.Build(new WeatherDisplayInput(
            inputs.CurrentTempC,
            new WeatherMetricsInput(
                inputs.Key.ShowFeelsLike, inputs.FeelsLikeC,
                inputs.Key.ShowHumidity, inputs.Humidity,
                inputs.Key.ShowWind, inputs.WindSpeedKmH,
                inputs.Key.ShowHighLow, inputs.HighTempC, inputs.LowTempC,
                tempUnit, speedUnit),
            inputs.Daily,
            inputs.Hourly));

        var model = new WeatherRenderModel
        {
            Key = inputs.Key,
            DataVersion = inputs.Key.DataVersion,
            Bounds = inputs.Key.Bounds,
            LayoutMode = inputs.Key.LayoutMode,
            UnitSystem = inputs.Key.UnitSystem,
            CustomLabel = inputs.Key.CustomLabel,
            ResolvedCity = inputs.Key.ResolvedCity,
            ShowFeelsLike = inputs.Key.ShowFeelsLike,
            ShowHumidity = inputs.Key.ShowHumidity,
            ShowWind = inputs.Key.ShowWind,
            ShowHighLow = inputs.Key.ShowHighLow,
            ShowForecast = inputs.Key.ShowForecast,
            CandidateCount = inputs.Key.CandidateCount,
            WeatherCode = inputs.WeatherCode,
            Daily = inputs.Daily.ToArray(),
            Hourly = inputs.Hourly.ToArray(),
            Display = display,
        };

        // Auto-truncated header: the city name (or the custom label) uppercased
        // once per model, then truncated to the same max width the draw path
        // uses — measured with the handed-in header geometry, never recomputed.
        string cityRaw = string.IsNullOrWhiteSpace(inputs.Key.CustomLabel) ? inputs.Key.ResolvedCity : inputs.Key.CustomLabel;
        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, inputs.Header.TitleFontSize);
        float maxTitleW = WeatherLayout.TitleMaxWidth(inputs.Key.Bounds.Width, inputs.Header.Pad, inputs.Header.BadgeRect.Width);
        model.TruncatedHeader = TextRenderHelper.TruncateText(cityRaw.ToUpperInvariant(), titleFont, maxTitleW);

        // Pill widths: measured with the pill font the draw path derives from
        // the same scale — via the renderer's shared helper, so the model's
        // cached widths and the draw path's shrink re-measure are ONE spelling.
        model.MetricWidths = WeatherWidgetRenderer.MeasurePillWidths(
            display.Metrics,
            WeatherLayout.PillFontSize(inputs.Scale),
            WeatherLayout.PillPadX(inputs.Scale));

        // Subtitle line below the header: the ONE spelling of guidance or
        // confirmation text. The priority order ensures the most actionable
        // message wins — a tie always beats "set a location", a custom label
        // always shows the resolved city for confirmation.
        bool isUnresolved = string.IsNullOrWhiteSpace(inputs.Key.ResolvedCity)
            || string.Equals(inputs.Key.ResolvedCity, WeatherFetchControl.UnknownLocationLabel, StringComparison.Ordinal);
        if (inputs.CandidateCount > 0 && inputs.Daily.Count == 0)
        {
            // Ambiguous tie: candidates exist but no weather data — the
            // Location Match dropdown is the documented escape route.
            model.SubtitleText = "Multiple cities found \u2014 pick one in Settings";
        }
        else if (isUnresolved && string.IsNullOrWhiteSpace(inputs.LocationText))
        {
            // No location set yet.
            model.SubtitleText = "Set a location in Settings";
        }
        else if (isUnresolved && !string.IsNullOrWhiteSpace(inputs.LocationText))
        {
            // Location was set but failed to resolve.
            model.SubtitleText = "Check spelling \u2014 try 'City, State' or 'City, Country'";
        }
        else if (!isUnresolved && !string.IsNullOrWhiteSpace(inputs.Key.CustomLabel)
                 && !string.Equals(inputs.Key.CustomLabel, inputs.Key.ResolvedCity, StringComparison.Ordinal))
        {
            // Custom label set: show the resolved city for confirmation.
            model.SubtitleText = inputs.Key.ResolvedCity;
        }

        return model;
    }
}