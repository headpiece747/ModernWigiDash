namespace ModernWigiDash.Widgets;

/// <summary>
/// The widget's snapshot-apply policy: the version-then-identity gate and the
/// null-keeps / per-list-version-bump merge that turn a fetched or cached
/// <see cref="WeatherSnapshot"/> into the widget's display state. Pure — no
/// lock, no fields — so the atomicity stays the display-state module's gate
/// and the rules are directly assertable. The module's TryApply is a thin
/// adapter: under its gate it asks <see cref="GuardsPass"/> first, then swaps
/// in <see cref="Merge"/>'s result.
/// </summary>
internal static class WeatherSnapshotApplyPolicy
{
    /// <summary>
    /// The version-then-identity gate: the apply is skipped when the data
    /// version moved on (a newer snapshot landed) and, only when the version
    /// still matches, when the identity guard fails. Version first keeps the
    /// identity predicate from running for a stale apply (a fetch landed
    /// while a cache load was in flight must never be overwritten by the
    /// stale cache — and the identity re-check rides the same gate).
    /// </summary>
    public static bool GuardsPass(int? expectedVersion, int dataVersion, Func<bool>? identityGuard)
        => (expectedVersion is not int expected || dataVersion == expected)
           && (identityGuard is null || identityGuard());

    /// <summary>
    /// Merges <paramref name="snapshot"/> into <paramref name="current"/> and
    /// returns the new state. The data version always bumps — the merge is
    /// the snapshot's commit. Null snapshot sections keep the previous value
    /// (a response that omitted a section must not blank the display), a
    /// provided forecast list replaces its slice and bumps the forecast
    /// version, and the two lists bump separately (both provided = two bumps).
    /// </summary>
    public static WeatherSnapshotState Merge(WeatherSnapshot snapshot, WeatherSnapshotState current)
    {
        var next = current with { DataVersion = current.DataVersion + 1 };
        if (snapshot.CurrentTempC is not null) next = next with { CurrentTempC = snapshot.CurrentTempC.Value };
        if (snapshot.FeelsLikeC is not null) next = next with { FeelsLikeC = snapshot.FeelsLikeC.Value };
        if (snapshot.Humidity is not null) next = next with { Humidity = snapshot.Humidity.Value };
        if (snapshot.WindSpeedKmH is not null) next = next with { WindSpeedKmH = snapshot.WindSpeedKmH.Value };
        if (snapshot.WeatherCode is not null) next = next with { WeatherCode = snapshot.WeatherCode.Value };
        if (snapshot.IsDay is bool isDay) next = next with { IsDay = isDay };
        if (snapshot.HighTempC is not null) next = next with { HighTempC = snapshot.HighTempC.Value };
        if (snapshot.LowTempC is not null) next = next with { LowTempC = snapshot.LowTempC.Value };
        if (snapshot.DailyForecasts is not null)
        {
            next = next with { DailyForecasts = snapshot.DailyForecasts, ForecastVersion = next.ForecastVersion + 1 };
        }
        if (snapshot.HourlyForecasts is not null)
        {
            next = next with { HourlyForecasts = snapshot.HourlyForecasts, ForecastVersion = next.ForecastVersion + 1 };
        }
        return next;
    }
}
