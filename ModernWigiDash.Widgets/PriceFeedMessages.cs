using System.Globalization;
using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>One parsed Finnhub trade.</summary>
public sealed record PriceFeedTrade(string Symbol, decimal Price);

/// <summary>
/// Pure parsers for the price-feed payloads (Binance WS/REST, Finnhub
/// WS/REST, CoinGecko, Frankfurter, Yahoo chart). Extracted from
/// PriceFeedManager's private message handlers and poll bodies so the wire
/// formats are directly testable; the manager keeps the writes, the clocks,
/// and the subscription state.
/// </summary>
public static class PriceFeedMessages
{
    /// <summary>
    /// Binance WS ticker: accepts both the nested <c>data</c> payload and the
    /// flat <c>e</c> shape; only USDT pairs parse. The coin is the symbol
    /// minus the USDT suffix, upper-cased.
    /// </summary>
    public static bool TryParseBinanceTicker(string json, out string coin, out decimal price, out decimal changePercent)
    {
        coin = "";
        price = 0m;
        changePercent = 0m;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string s, c, P;
            if (root.TryGetProperty("data", out var data))
            {
                s = data.GetProperty("s").GetString() ?? "";
                c = data.GetProperty("c").GetString() ?? "";
                P = data.GetProperty("P").GetString() ?? "";
            }
            else if (root.TryGetProperty("e", out _))
            {
                s = root.GetProperty("s").GetString() ?? "";
                c = root.GetProperty("c").GetString() ?? "";
                P = root.GetProperty("P").GetString() ?? "";
            }
            else
            {
                return false;
            }

            if (!s.EndsWith("USDT", StringComparison.Ordinal)
                || !decimal.TryParse(c, NumberStyles.Any, CultureInfo.InvariantCulture, out price)
                || !decimal.TryParse(P, NumberStyles.Any, CultureInfo.InvariantCulture, out changePercent))
            {
                return false;
            }

            coin = s[..^4].ToUpperInvariant();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finnhub WS <c>trade</c> message: the <c>data</c> array's symbol/price
    /// pairs. Non-trade messages parse as empty.
    /// </summary>
    public static bool TryParseFinnhubTrades(string json, out IReadOnlyList<PriceFeedTrade> trades)
    {
        trades = [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!string.Equals(root.GetProperty("type").GetString(), "trade", StringComparison.Ordinal) || !root.TryGetProperty("data", out var data))
            {
                return true; // a well-formed non-trade message is not an error
            }

            List<PriceFeedTrade> parsed = [];
            foreach (var trade in data.EnumerateArray())
            {
                parsed.Add(new PriceFeedTrade(
                    trade.GetProperty("s").GetString() ?? "",
                    trade.GetProperty("p").GetDecimal()));
            }
            trades = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Binance REST 24hr ticker: <c>lastPrice</c> / <c>priceChangePercent</c> as strings.</summary>
    public static bool TryParseBinanceRestTicker(string json, out decimal price, out decimal changePercent)
    {
        price = 0m;
        changePercent = 0m;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("lastPrice", out var lp) || !root.TryGetProperty("priceChangePercent", out var pcp))
            {
                return false;
            }
            return decimal.TryParse(lp.GetString() ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out price)
                && decimal.TryParse(pcp.GetString() ?? "", NumberStyles.Any, CultureInfo.InvariantCulture, out changePercent);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Finnhub REST quote: <c>c</c> (current) with a nullable <c>dp</c> (day change %).</summary>
    public static bool TryParseFinnhubQuote(string json, out decimal price, out decimal? changePercent)
    {
        price = 0m;
        changePercent = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("c", out var c))
            {
                return false;
            }
            price = c.GetDecimal();
            if (root.TryGetProperty("dp", out var dp) && dp.ValueKind != JsonValueKind.Null)
            {
                changePercent = dp.GetDecimal();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// CoinGecko simple-price batch: id → (price, change). Parses the
    /// document once for the fallback loop that stores every subscribed
    /// alias — the per-id spelling would re-parse the same JSON per alias.
    /// Entries missing their id (or a null usd) are absent from the result;
    /// a malformed document yields an empty result.
    /// </summary>
    public static IReadOnlyDictionary<string, (decimal Price, decimal? ChangePercent)> ParseCoinGeckoSimplePriceBatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = new Dictionary<string, (decimal, decimal?)>();
            foreach (var coin in doc.RootElement.EnumerateObject())
            {
                if (!coin.Value.TryGetProperty("usd", out var usdEl) || usdEl.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }
                decimal? change = null;
                if (coin.Value.TryGetProperty("usd_24h_change", out var changeEl) && changeEl.ValueKind != JsonValueKind.Null)
                {
                    change = changeEl.GetDecimal();
                }
                result[coin.Name] = (usdEl.GetDecimal(), change);
            }
            return result;
        }
        catch
        {
            return new Dictionary<string, (decimal, decimal?)>();
        }
    }

    /// <summary>CoinGecko simple-price response: <c>usd</c> plus a nullable <c>usd_24h_change</c>.</summary>
    public static bool TryParseCoinGeckoSimplePrice(string json, string coinGeckoId, out decimal price, out decimal? changePercent)
    {
        price = 0m;
        changePercent = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty(coinGeckoId, out var coin)
                || !coin.TryGetProperty("usd", out var usdEl)
                || usdEl.ValueKind == JsonValueKind.Null)
            {
                return false;
            }
            price = usdEl.GetDecimal();
            if (coin.TryGetProperty("usd_24h_change", out var changeEl) && changeEl.ValueKind != JsonValueKind.Null)
            {
                changePercent = changeEl.GetDecimal();
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a Frankfurter (ECB) daily-rate series. The last entry is the current rate; the
    /// day-over-day change percent is computed from the last two entries.
    /// </summary>
    internal static bool TryParseFrankfurterSeries(string json, string quoteCurrency, out decimal price, out decimal changePercent)
    {
        price = 0m;
        changePercent = 0m;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("rates", out var rates) || rates.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            List<string> dates = [];
            var ratesByDate = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var entry in rates.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty(quoteCurrency, out var rateEl) || rateEl.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }
                dates.Add(entry.Name);
                ratesByDate[entry.Name] = rateEl.GetDecimal();
            }

            if (dates.Count == 0)
            {
                return false;
            }

            dates.Sort(StringComparer.Ordinal); // ISO yyyy-MM-dd sorts chronologically.
            price = ratesByDate[dates[^1]];
            if (dates.Count >= 2 && ratesByDate[dates[^2]] != 0m)
            {
                changePercent = (ratesByDate[dates[^1]] / ratesByDate[dates[^2]] - 1m) * 100m;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Yahoo chart response: the first result's <c>meta</c> holds the regular
    /// market price and the previous close; the change percent is derived from
    /// them (zero when the previous close is unknown).
    /// </summary>
    internal static bool TryParseYahooChart(string json, out decimal price, out decimal changePercent)
    {
        price = 0m;
        changePercent = 0m;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
            var meta = result.GetProperty("meta");
            price = (decimal)meta.GetProperty("regularMarketPrice").GetDouble();
            decimal prevClose = (decimal)meta.GetProperty("chartPreviousClose").GetDouble();
            changePercent = prevClose != 0 ? (price - prevClose) / prevClose * 100m : 0m;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
