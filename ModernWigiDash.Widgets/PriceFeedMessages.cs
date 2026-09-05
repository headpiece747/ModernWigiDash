using System.Text.Json;

namespace ModernWigiDash.Widgets;

/// <summary>One parsed Finnhub trade.</summary>
public sealed record PriceFeedTrade(string Symbol, decimal Price);

/// <summary>
/// Pure wire-format rules for the price feeds: the WebSocket subscribe
/// frames (the request side) and the payload parsers (the response side:
/// Binance WS/REST, Finnhub WS/REST, CoinGecko, Frankfurter, Yahoo chart).
/// Extracted from PriceFeedManager's private message handlers, poll bodies,
/// and subscribe payloads so the wire formats are directly testable; the
/// manager keeps the writes, the clocks, and the subscription state.
/// </summary>
public static class PriceFeedMessages
{
    /// <summary>
    /// The Binance US stream key for a base coin: the lower-cased base coin
    /// plus the usdt ticker stream (e.g. "BTC" to "btcusdt@ticker"). One
    /// spelling, shared by the connect-time bulk send and the incremental
    /// per-symbol send.
    /// </summary>
    public static string BinanceStreamKey(string baseCoin) => $"{baseCoin.ToLowerInvariant()}usdt@ticker";

    /// <summary>
    /// One Binance US subscribe frame: a single SUBSCRIBE carrying every
    /// stream key with the fixed request id. One spelling for the
    /// connect-time bulk send and the incremental per-symbol send, so the
    /// two can never drift.
    /// </summary>
    public static string BuildBinanceSubscribe(IEnumerable<string> baseCoins) =>
        JsonSerializer.Serialize(new { method = "SUBSCRIBE", @params = baseCoins.Select(BinanceStreamKey).ToArray(), id = 1 });

    /// <summary>
    /// One Finnhub subscribe frame: the protocol is one frame per symbol.
    /// One spelling for the connect-time bulk send and the incremental
    /// per-symbol send.
    /// </summary>
    public static string BuildFinnhubSubscribe(string symbol) =>
        JsonSerializer.Serialize(new { type = "subscribe", symbol });
    /// <summary>
    /// Binance WS ticker: accepts both the nested <c>data</c> payload and the
    /// flat <c>e</c> shape; only USDT pairs parse. The coin is the symbol
    /// minus the USDT suffix, upper-cased. Parsed with a streaming reader over
    /// the UTF-8 bytes so the per-message path allocates no DOM tree (the
    /// high-frequency WebSocket leg): every root property is collected into one
    /// small dictionary, then the fields are read from whichever shape is
    /// present. Property order does not matter (a field may precede <c>e</c>).
    /// </summary>
    public static bool TryParseBinanceTicker(string json, out string coin, out decimal price, out decimal changePercent)
    {
        coin = "";
        price = 0m;
        changePercent = 0m;
        try
        {
            var props = ReadObjectProperties(json);
            if (props is null)
            {
                return false;
            }

            // Nested shape reads s/c/P from the "data" object; the flat shape
            // reads them from the root. A message is one or the other.
            Dictionary<string, object?>? source;
            if (props.TryGetValue("data", out var dataObj) && dataObj is Dictionary<string, object?> nested)
            {
                source = nested;
            }
            else if (props.ContainsKey("e"))
            {
                source = props;
            }
            else
            {
                return false;
            }

            string? s = source.GetValueOrDefault("s") as string;
            string? c = source.GetValueOrDefault("c") as string;
            string? P = source.GetValueOrDefault("P") as string;
            if (s is null || !s.EndsWith("USDT", StringComparison.Ordinal)
                || c is null || P is null
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
    /// Streams a top-level JSON object into a property map without building a
    /// DOM tree. Object-valued properties are themselves collected as nested
    /// maps (one level deep, which is all the price payloads need); scalar
    /// values are kept as strings. Returns null when the input is not a JSON
    /// object. This is the allocation-light replacement for building a DOM
    /// tree on the per-message hot path.
    /// </summary>
    private static Dictionary<string, object?>? ReadObjectProperties(string json)
    {
        ReadOnlySpan<byte> utf8 = System.Text.Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(utf8);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            string name = reader.GetString()!;
            reader.Read();
            result[name] = reader.TokenType switch
            {
                JsonTokenType.StartObject => ReadNestedStringMap(ref reader),
                _ => reader.GetString(),
            };
        }
        return result;
    }

    /// <summary>Reads one nested object of string properties, skipping deeper nesting.</summary>
    private static Dictionary<string, object?> ReadNestedStringMap(ref Utf8JsonReader reader)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        int depth = 1;
        while (depth > 0 && reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                depth++;
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }
            }
            else if (depth == 1 && reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString()!;
                reader.Read();
                map[key] = reader.TokenType == JsonTokenType.StartObject ? null : reader.GetString();
            }
        }
        return map;
    }

    /// <summary>
    /// Finnhub WS <c>trade</c> message: the <c>data</c> array's symbol/price
    /// pairs. Non-trade messages parse as empty. Parsed with a streaming reader
    /// over the UTF-8 bytes so the per-message path allocates no DOM tree (the
    /// high-frequency WebSocket leg).
    /// </summary>
    public static bool TryParseFinnhubTrades(string json, out IReadOnlyList<PriceFeedTrade> trades)
    {
        trades = [];
        try
        {
            ReadOnlySpan<byte> utf8 = System.Text.Encoding.UTF8.GetBytes(json);
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            string? type = null;
            List<PriceFeedTrade>? parsed = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    string prop = reader.GetString()!;
                    reader.Read();
                    if (string.Equals(prop, "type", StringComparison.Ordinal))
                    {
                        type = reader.GetString();
                    }
                    else if (string.Equals(prop, "data", StringComparison.Ordinal) && reader.TokenType == JsonTokenType.StartArray)
                    {
                        parsed = ReadFinnhubTradesArray(ref reader);
                    }
                }
            }

            if (!string.Equals(type, "trade", StringComparison.Ordinal) || parsed is null)
            {
                return true; // a well-formed non-trade message is not an error
            }

            trades = parsed;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the <c>data</c> array of <c>{s, p}</c> trade objects into records.</summary>
    private static List<PriceFeedTrade> ReadFinnhubTradesArray(ref Utf8JsonReader reader)
    {
        var list = new List<PriceFeedTrade>();
        int depth = 0; // 0 = inside the data array, 1 = inside a trade object.
        string? symbol = null;
        decimal price = 0m;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                    depth--;
                    if (depth == 0)
                    {
                        // Finished one trade object: emit it and reset.
                        list.Add(new PriceFeedTrade(symbol ?? "", price));
                        symbol = null;
                        price = 0m;
                    }
                    break;
                case JsonTokenType.EndArray:
                    return list;
                case JsonTokenType.PropertyName when depth == 1:
                    string name = reader.GetString()!;
                    reader.Read();
                    if (string.Equals(name, "s", StringComparison.Ordinal))
                    {
                        symbol = reader.GetString() ?? "";
                    }
                    else if (string.Equals(name, "p", StringComparison.Ordinal))
                    {
                        price = reader.GetDecimal();
                    }
                    break;
            }
        }
        return list;
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
