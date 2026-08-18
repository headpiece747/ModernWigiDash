using System.Globalization;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The Frankfurter (ECB) daily-rate series leg (the FX REST cycle): the
/// date window is built at poll time from the live clock (10 days back
/// through today, invariant culture), the parse reads the last two dates
/// for the day-over-day change, and the currency symbol is empty — cross
/// rates carry no currency. The generic fetch → parse hop is the
/// <see cref="PriceRestLeg"/> machine; the price-map store policy stays
/// with the manager.
/// </summary>
internal static class FrankfurterRestLeg
{
    /// <summary>The price-map source label Frankfurter rates are stored
    /// under.</summary>
    internal const string SourceLabel = "Frankfurter";

    /// <summary>Builds the leg over the given fetch-time clock — read per
    /// fetch, so a caller that swaps its clock after construction is
    /// honored (the manager passes its own
    /// <see cref="PriceFeedManager.Clock"/>)</summary>
    internal static PriceRestLeg Create(HttpClient http, Func<DateTimeOffset> now)
        => new(http, SourceLabel, "",
            key => BuildUrl(key, now().UtcDateTime),
            (json, key) => PriceFeedMessages.TryParseFrankfurterSeries(json, key[3..], out var price, out var change)
                ? new QuoteSample(price, change) : null,
            SymbolCatalog.IsValidFxKey);

    private static string BuildUrl(string key, DateTime now)
    {
        string start = now.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string end = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"https://api.frankfurter.app/{start}..{end}?from={key[..3]}&to={key[3..]}";
    }
}
