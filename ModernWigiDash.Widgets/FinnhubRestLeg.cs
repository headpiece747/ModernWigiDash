namespace ModernWigiDash.Widgets;

/// <summary>
/// The Finnhub quote leg (the stock REST cycle): the API key rides the
/// URL and the quote parse owns the nullable day-change. The generic
/// fetch → parse hop is the <see cref="PriceRestLeg"/> machine; the
/// price-map store policy stays with the manager.
/// </summary>
internal static class FinnhubRestLeg
{
    /// <summary>The price-map source label Finnhub quotes are stored
    /// under.</summary>
    internal const string SourceLabel = "Finnhub";

    /// <summary>Builds the leg for the given API key (an empty key is
    /// legal — the stock cycle only starts when the manager's key is set,
    /// so an unconfigured leg is never fetched).</summary>
    internal static PriceRestLeg Create(HttpClient http, string apiKey)
        => new(http, SourceLabel, "$",
            key => $"https://finnhub.io/api/v1/quote?symbol={key}&token={apiKey}",
            (json, _) => PriceFeedMessages.TryParseFinnhubQuote(json, out var price, out var change)
                ? new QuoteSample(price, change) : null,
            SymbolCatalog.IsValidSymbol);
}
