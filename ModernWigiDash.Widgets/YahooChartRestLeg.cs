namespace ModernWigiDash.Widgets;

/// <summary>
/// The Yahoo chart leg — the stock one-shot fallback only (stocks ride
/// Finnhub on the REST cycle): the 1d chart's meta block carries the
/// regular market price and the previous close, and the change percent is
/// derived from them. The generic fetch → parse hop is the
/// <see cref="PriceRestLeg"/> machine; the price-map store policy stays
/// with the manager's <see cref="PriceMapStore"/> seam.
/// </summary>
internal static class YahooChartRestLeg
{
    /// <summary>The price-map source label Yahoo quotes are stored
    /// under.</summary>
    internal const string SourceLabel = "Yahoo";

    /// <summary>Builds the leg: the symbol's 1d chart URL, the meta-block
    /// parse, and the catalog's symbol guard (the one-shot seed path has
    /// no subscription boundary to validate at).</summary>
    internal static PriceRestLeg Create(HttpClient http)
        => new(http, SourceLabel, "$",
            key => $"https://query1.finance.yahoo.com/v8/finance/chart/{key}?interval=1d&range=1d",
            (json, _) => PriceFeedMessages.TryParseYahooChart(json, out var price, out var change)
                ? new QuoteSample(price, change) : null,
            SymbolCatalog.IsValidSymbol);
}
