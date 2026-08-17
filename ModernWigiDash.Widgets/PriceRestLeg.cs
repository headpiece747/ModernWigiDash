namespace ModernWigiDash.Widgets;

/// <summary>
/// One quote result from a REST leg: the price plus the optional change
/// percent the source reports (null when the source has no change figure).
/// </summary>
internal readonly record struct QuoteSample(decimal Price, decimal? ChangePercent);

/// <summary>
/// One per-symbol REST quote leg — the source adapter behind the manager's
/// shared HTTP seam: validation guard → URL build for the symbol → one GET →
/// the source's wire parse → the quote sample. The leg owns the source's
/// wire format (URL shape + response parse, the parsers live in
/// <see cref="PriceFeedMessages"/>), never the price map — the manager
/// applies the sample with its source-specific merge policy, so a URL or
/// parse bug localizes in one construction site per source.
/// </summary>
internal sealed class PriceRestLeg(
    HttpClient http,
    string sourceLabel,
    string currencySymbol,
    Func<string, string> buildUrl,
    Func<string, string, QuoteSample?> parseJson,
    Predicate<string>? validate = null)
{
    /// <summary>The price-map source label the leg's quotes are stored under.</summary>
    internal string SourceLabel { get; } = sourceLabel;

    /// <summary>The price-map currency symbol for this source (Frankfurter's
    /// cross rates carry none).</summary>
    internal string CurrencySymbol { get; } = currencySymbol;

    /// <summary>
    /// One fetch → parse hop for a symbol. Returns null when the symbol fails
    /// the validation guard or the response does not parse as a quote — in
    /// either case nothing is stored; a transport failure propagates and the
    /// loop owner isolates it per symbol.
    /// </summary>
    internal Task<QuoteSample?> FetchAsync(string key, CancellationToken ct)
    {
        if (validate is not null && !validate(key))
        {
            return Task.FromResult<QuoteSample?>(null);
        }

        return FetchAsyncCore(key, ct);
    }

    private async Task<QuoteSample?> FetchAsyncCore(string key, CancellationToken ct)
    {
        string json = await http.GetStringAsync(buildUrl(key), ct).ConfigureAwait(false);
        return parseJson(json, key);
    }
}
