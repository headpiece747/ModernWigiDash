namespace ModernWigiDash.Widgets;

/// <summary>
/// The render-model inputs: the cache key (the value snapshot of everything
/// that can change the formatted strings), the consistent snapshot display
/// view (read under the host's gate — the data scalars, the weather code and
/// its day/night flag, and the two copied forecast lists), and the per-frame
/// geometry (the header
/// layout and the scale, computed ONCE in the render tick and shared with the
/// draw path — the build never re-derives the geometry it is handed).
/// </summary>
internal sealed record WeatherRenderModelInputs(
    WeatherRenderModelKey Key,
    int WeatherCode,
    bool IsDay,
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
    int CandidateCount,
    string NeutralLabel);

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
            // The key rides by reference: it is the model's single identity, and the
            // property snapshot the draw paths read comes from it — a
            // field-by-field copy could drift by a forgotten line here.
            Key = inputs.Key,
            WeatherCode = inputs.WeatherCode,
            IsDay = inputs.IsDay,
            Daily = inputs.Daily.ToArray(),
            Hourly = inputs.Hourly.ToArray(),
            Display = display,
        };

        // Auto-truncated header: the city name (or the custom label) uppercased
        // once per model, then truncated to the same max width the draw path
        // uses — measured with the handed-in header geometry, never recomputed.
        // A blank resolved city (no resolution yet, e.g. after a location
        // edit's drop) falls back to the injected neutral label, so the header
        // matches the fresh-widget seed instead of rendering blank: the same
        // logical state ("no resolution") shows the neutral label whether or
        // not a drop has run. The subtitle already treats blank as unresolved.
        string cityRaw;
        if (!string.IsNullOrWhiteSpace(inputs.Key.CustomLabel))
        {
            cityRaw = inputs.Key.CustomLabel;
        }
        else if (!string.IsNullOrWhiteSpace(inputs.Key.ResolvedCity))
        {
            cityRaw = inputs.Key.ResolvedCity;
        }
        else
        {
            cityRaw = inputs.NeutralLabel;
        }
        var titleFont = WeatherWidgetRenderer.GetTitleFont(inputs.Header.TitleFontSize);
        float maxTitleW = WeatherLayout.TitleMaxWidth(inputs.Key.Bounds.Width, inputs.Header.Pad, inputs.Header.BadgeRect.Width);
        model.TruncatedHeader = TextRenderHelper.TruncateText(cityRaw.ToUpperInvariant(), titleFont, maxTitleW);

        // Pill widths: measured with the pill font the draw path derives from
        // the same scale — via the renderer's shared helper, so the model's
        // cached widths and the draw path's shrink re-measure are ONE spelling.
        model.MetricWidths = WeatherWidgetRenderer.MeasurePillWidths(
            display.Metrics,
            WeatherLayout.PillFontSize(inputs.Scale),
            WeatherLayout.PillPadX(inputs.Scale));

        // Subtitle line below the header: the guidance or confirmation text and its
        // priority rule live in the presentation module (the unresolved
        // verdict included — the build module no longer reaches into
        // fetch-control state for a display fact).
        model.SubtitleText = WeatherPresentation.BuildSubtitle(
            inputs.Key.ResolvedCity, inputs.Key.CustomLabel, inputs.LocationText,
            inputs.CandidateCount, inputs.Daily.Count);

        return model;
    }
}
