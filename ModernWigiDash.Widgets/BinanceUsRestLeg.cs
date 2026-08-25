namespace ModernWigiDash.Widgets;

/// <summary>
/// The BinanceUS 24h-ticker leg (the crypto REST cycle): the URL shape,
/// the wire parse, the source label and the currency symbol in one place —
/// the source's wire format lives with the source. The generic fetch →
/// parse hop is the <see cref="PriceRestLeg"/> machine; the price-map store
/// policy stays with the manager's <see cref="PriceMapStore"/> seam.
/// </summary>
internal static class BinanceUsRestLeg
{
    /// <summary>The price-map source label BinanceUS quotes are stored
    /// under — the freshness guard's discriminator, so the label lives with
    /// the source it labels (a rename here is the one that may touch the
    /// downgrade rule).</summary>
    internal const string SourceLabel = "BinanceUS";

    /// <summary>Builds the leg: the symbol's USDT-paired 24hr-ticker URL,
    /// the ticker's parse (last price + change percent as strings), and no
    /// validation guard — the crypto subscription boundary already
    /// validated the symbol.</summary>
    internal static PriceRestLeg Create(HttpClient http)
        => new(http, SourceLabel, "$",
            key => $"https://api.binance.us/api/v3/ticker/24hr?symbol={key}USDT",
            (json, _) => PriceFeedMessages.TryParseBinanceRestTicker(json, out var price, out var change)
                ? new QuoteSample(price, change) : null);
}
