namespace ModernWigiDash.Widgets;

/// <summary>
/// The CoinGecko simple-price leg: one URL shape for both consumers — the
/// crypto cycle's batch fallback (every resolvable subscribed base coin in a
/// single ids= request, parsed once) and the widget's one-shot fetch (one
/// id resolved from the <see cref="SymbolCatalog"/> crypto table). The leg
/// owns the wire format; the price-map merge policy (including the BinanceUS
/// freshness guard) stays with the manager's <see cref="PriceMapStore"/>
/// seam, which applies the samples.
/// </summary>
internal sealed class CoinGeckoRestLeg(HttpClient http)
{
    /// <summary>The price-map source label for CoinGecko quotes (the one
    /// spelling shared by the one-shot and batch store sites).</summary>
    internal const string SourceLabel = "CoinGecko";

    /// <summary>One simple-price URL for a CoinGecko id or a comma-joined id set.</summary>
    internal static string SimplePriceUrl(string coinGeckoId)
        => $"https://api.coingecko.com/api/v3/simple/price?ids={coinGeckoId}&vs_currencies=usd&include_24hr_change=true";

    /// <summary>
    /// The one-shot fetch: resolves the coin's CoinGecko id from the catalog
    /// (an unknown coin makes no request) and parses the single-id response.
    /// </summary>
    internal Task<QuoteSample?> FetchAsync(string baseCoin, CancellationToken ct)
    {
        if (SymbolCatalog.CoinGeckoIdFor(baseCoin) is not string id)
        {
            return Task.FromResult<QuoteSample?>(null);
        }

        return FetchAsyncCore(SimplePriceUrl(id), id, ct);
    }

    /// <summary>
    /// The cycle batch: one request for the distinct CoinGecko ids among
    /// <paramref name="baseCoins"/> (an alias and its canonical coin resolve
    /// to one id), one parse, returned as base-coin-keyed samples. Null when
    /// no coin resolves (no request is made) or nothing parses.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, QuoteSample>?> FetchBatchAsync(IEnumerable<string> baseCoins, CancellationToken ct)
    {
        var idPairs = baseCoins
            .Select(c => (Coin: c, Id: SymbolCatalog.CoinGeckoIdFor(c)))
            .Where(p => p.Id is not null)
            .GroupBy(p => p.Id!)
            .Select(g => (Id: g.Key, Coin: g.First().Coin))
            .ToList();
        if (idPairs.Count == 0)
        {
            return null;
        }

        string json = await http.GetStringAsync(SimplePriceUrl(string.Join(",", idPairs.Select(p => p.Id))), ct).ConfigureAwait(false);
        var parsed = PriceFeedMessages.ParseCoinGeckoSimplePriceBatch(json);
        var samples = new Dictionary<string, QuoteSample>(StringComparer.Ordinal);
        foreach (var pair in idPairs)
        {
            if (parsed.TryGetValue(pair.Id, out var value))
            {
                samples[pair.Coin] = new QuoteSample(value.Price, value.ChangePercent);
            }
        }
        return samples.Count > 0 ? samples : null;
    }

    private async Task<QuoteSample?> FetchAsyncCore(string url, string coinGeckoId, CancellationToken ct)
    {
        string json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        return PriceFeedMessages.TryParseCoinGeckoSimplePrice(json, coinGeckoId, out var price, out var change)
            ? new QuoteSample(price, change)
            : null;
    }
}
