using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

public enum AssetKind
{
    Crypto,
    Stock,
    Fx
}

public class PriceInfo
{
    public decimal Price { get; set; }
    public decimal ChangePercent { get; set; }
    public string CurrencySymbol { get; set; } = "$";
    public string Source { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string FormattedPrice => $"{CurrencySymbol}{Price:N2}";
    public string FormattedChange => $"{(ChangePercent >= 0 ? "+" : "")}{ChangePercent:F2}%";
    public bool IsPositive => ChangePercent >= 0;
    public bool IsStale => (TimeProvider.System.GetUtcNow().UtcDateTime - Timestamp).TotalSeconds > 60;
}

public sealed class PriceFeedManager : IDisposable
{
    private enum WebSocketFeed
    {
        Binance,
        Finnhub
    }

    private static readonly FrozenDictionary<string, string> CryptoMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["bitcoin"] = "BTC",
        ["btc"] = "BTC",
        ["ethereum"] = "ETH",
        ["eth"] = "ETH",
        ["solana"] = "SOL",
        ["sol"] = "SOL",
        ["dogecoin"] = "DOGE",
        ["doge"] = "DOGE",
        ["cardano"] = "ADA",
        ["ada"] = "ADA",
        ["ripple"] = "XRP",
        ["xrp"] = "XRP",
        ["polkadot"] = "DOT",
        ["dot"] = "DOT",
        ["litecoin"] = "LTC",
        ["ltc"] = "LTC",
        ["avalanche-2"] = "AVAX",
        ["avax"] = "AVAX",
        ["chainlink"] = "LINK",
        ["link"] = "LINK",
        ["polygon"] = "POL",
        ["pol"] = "POL",
        ["matic-network"] = "MATIC",
        ["matic"] = "MATIC",
        ["tron"] = "TRX",
        ["trx"] = "TRX",
        ["shiba-inu"] = "SHIB",
        ["shib"] = "SHIB",
        ["uniswap"] = "UNI",
        ["uni"] = "UNI",
        ["cosmos"] = "ATOM",
        ["atom"] = "ATOM",
        ["near"] = "NEAR",
        ["aptos"] = "APT",
        ["apt"] = "APT",
        ["arbitrum"] = "ARB",
        ["optimism"] = "OP",
        ["sui"] = "SUI",
        ["render"] = "RNDR",
        ["rndr"] = "RNDR",
        ["filecoin"] = "FIL",
        ["fil"] = "FIL",
        ["theta"] = "THETA",
        ["bnb"] = "BNB",
        ["toncoin"] = "TON",
        ["ton"] = "TON",
        ["mantle"] = "MNT",
        ["mnt"] = "MNT",
        ["injective"] = "INJ",
        ["inj"] = "INJ",
        ["pepe"] = "PEPE",
        ["floki"] = "FLOKI",
        ["bonk"] = "BONK",
        ["hedera"] = "HBAR",
        ["hbar"] = "HBAR",
        ["vechain"] = "VET",
        ["vet"] = "VET",
        ["aave"] = "AAVE",
        ["maker"] = "MKR",
        ["mkr"] = "MKR",
        ["curve"] = "CRV",
        ["crv"] = "CRV",
        ["eos"] = "EOS",
        ["fetch"] = "FET",
        ["fetch-ai"] = "FET",
        ["the-graph"] = "GRT",
        ["grt"] = "GRT",
        ["sei"] = "SEI",
        ["starknet"] = "STRK",
        ["strk"] = "STRK",
        ["immutable"] = "IMX",
        ["imx"] = "IMX",
        ["dydx"] = "DYDX",
        ["pendle"] = "PENDLE",
        ["kaspa"] = "KAS",
        ["kas"] = "KAS",
        ["fantom"] = "FTM",
        ["ftm"] = "FTM",
        ["algorand"] = "ALGO",
        ["algo"] = "ALGO",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> KnownCryptos = CryptoMap.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly string _finnhubKey;
    private readonly ConcurrentDictionary<string, PriceInfo> _prices = new();
    private readonly ConcurrentDictionary<string, byte> _subscribedCrypto = new();
    private readonly ConcurrentDictionary<string, byte> _subscribedStocks = new();
    private readonly ConcurrentDictionary<string, byte> _subscribedFx = new();
    private readonly HttpClient _http = new();
    private readonly TimeSpan _stockRestInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _cryptoRestInterval = TimeSpan.FromSeconds(30);

    private ClientWebSocket? _binanceWs;
    private ClientWebSocket? _finnhubWs;
    private readonly CancellationTokenSource _cts = new();
    private Task? _binanceTask;
    private Task? _finnhubTask;
    private Task? _stockRestTask;
    private Task? _cryptoRestTask;
    private Task? _fxRestTask;
    private bool _disposed;

    public PriceFeedManager(string? finnhubApiKey = null)
    {
        // The Finnhub key must come from an explicit argument or the
        // FINNHUB_API_KEY environment variable — never from source control.
        _finnhubKey = finnhubApiKey ?? Environment.GetEnvironmentVariable("FINNHUB_API_KEY") ?? "";
        if (string.IsNullOrEmpty(_finnhubKey))
        {
            FileLog.Write("[PRICE-FEED] FINNHUB_API_KEY not configured — stock WebSocket/REST feeds disabled. Set the FINNHUB_API_KEY environment variable or pass the key to the constructor. Yahoo Finance fallback still works.");
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ModernWigiDash/2.0");
    }

    public static bool IsCrypto(string symbol) => KnownCryptos.Contains(symbol);
    public static string NormalizeSymbol(string symbol) =>
        CryptoMap.TryGetValue(symbol, out var baseCoin) ? baseCoin : symbol.ToUpper();

    /// <summary>
    /// Maps a user-entered symbol to the canonical feed key for an asset kind:
    /// crypto aliases resolve to the base coin (e.g. "bitcoin" → "BTC"), FX
    /// pairs to "EURUSD", everything else to the upper-cased symbol.
    /// </summary>
    public static string ToFeedKey(string symbol, AssetKind kind) => kind switch
    {
        AssetKind.Crypto => CryptoMap.TryGetValue(symbol, out var baseCoin) ? baseCoin : symbol.ToUpper(),
        AssetKind.Fx => NormalizeFxKey(symbol),
        _ => symbol.ToUpper()
    };

    private static readonly Regex FxPairRegex = new("^([A-Za-z]{3})/([A-Za-z]{3})$", RegexOptions.Compiled);

    public static bool TryParseFxPair(string symbol, out string baseCurrency, out string quoteCurrency)
    {
        baseCurrency = "";
        quoteCurrency = "";
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        Match match = FxPairRegex.Match(symbol.Trim());
        if (!match.Success) return false;
        baseCurrency = match.Groups[1].Value.ToUpperInvariant();
        quoteCurrency = match.Groups[2].Value.ToUpperInvariant();
        return true;
    }

    public static string NormalizeFxKey(string symbol)
        => symbol.Trim().ToUpperInvariant().Replace("/", "", StringComparison.Ordinal);

    public static AssetKind DetectAssetKind(string symbol, string assetType) => assetType switch
    {
        "Crypto" => AssetKind.Crypto,
        "Stock" => AssetKind.Stock,
        "FX Pair" => AssetKind.Fx,
        _ when TryParseFxPair(symbol, out _, out _) => AssetKind.Fx,
        _ => IsCrypto(symbol) ? AssetKind.Crypto : AssetKind.Stock
    };

    public void Subscribe(string symbol, AssetKind kind)
    {
        switch (kind)
        {
            case AssetKind.Crypto:
                var baseCoin = ToFeedKey(symbol, kind);
                if (_subscribedCrypto.TryAdd(baseCoin, 0))
                {
                    _binanceTask ??= RunBinanceLoopAsync();
                    _cryptoRestTask ??= RunCryptoRestPollerAsync();
                    // Push an incremental subscribe so symbols added after the
                    // socket connected still receive real-time ticks.
                    _ = SendWsSubscribeAsync(WebSocketFeed.Binance, $"{baseCoin.ToLower()}usdt@ticker");
                }
                break;
            case AssetKind.Fx:
                string fxKey = NormalizeFxKey(symbol);
                if (_subscribedFx.TryAdd(fxKey, 0))
                {
                    _fxRestTask ??= RunFxRestPollerAsync();
                }
                break;
            default:
                string stockSym = symbol.ToUpper();
                if (_subscribedStocks.TryAdd(stockSym, 0))
                {
                    // Without a Finnhub key the WS/REST stock feeds cannot work;
                    // the Yahoo Finance fallback in FetchFallbackAsync still does.
                    if (!string.IsNullOrEmpty(_finnhubKey))
                    {
                        _finnhubTask ??= RunFinnhubLoopAsync();
                        _stockRestTask ??= RunStockRestPollerAsync();
                        _ = SendWsSubscribeAsync(WebSocketFeed.Finnhub, stockSym);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Sends an incremental WebSocket subscription for a symbol added after the
    /// feed socket was already connected. No-op when the socket is not open.
    /// </summary>
    private async Task SendWsSubscribeAsync(WebSocketFeed feed, string payload)
    {
        try
        {
            ClientWebSocket? ws = feed == WebSocketFeed.Finnhub ? _finnhubWs : _binanceWs;
            if (ws == null || ws.State != WebSocketState.Open) return;
            object message = feed == WebSocketFeed.Finnhub
                ? new { type = "subscribe", symbol = payload }
                : new { method = "SUBSCRIBE", @params = new[] { payload }, id = 1 }; await SendJsonAsync(ws, message);
        }
        catch
        {
            // Incremental subscribe is best-effort; the connect-time payload
            // covers the symbols known at that point.
            System.Diagnostics.Debug.WriteLine($"Incremental feed subscribe failed for {payload}");
        }
    }

    /// <summary>
    /// Stops polling for <paramref name="symbol"/> (e.g. after the widget was
    /// removed from the canvas). The underlying loops keep running while any
    /// symbol remains subscribed, and stop entirely when the last one leaves.
    /// </summary>
    public void Unsubscribe(string symbol, AssetKind kind)
    {
        switch (kind)
        {
            case AssetKind.Crypto:
                _subscribedCrypto.TryRemove(ToFeedKey(symbol, kind), out _);
                break;
            case AssetKind.Fx:
                _subscribedFx.TryRemove(NormalizeFxKey(symbol), out _);
                break;
            default:
                _subscribedStocks.TryRemove(symbol.ToUpper(), out _);
                break;
        }
    }

    public PriceInfo? GetPrice(string symbol, AssetKind kind)
    {
        string key = ToFeedKey(symbol, kind);
        return _prices.TryGetValue(key, out var info) ? info : null;
    }

    /// <summary>
    /// One-shot fallback price fetch for a single symbol (crypto via CoinGecko
    /// using the CoinGeckoIds mapping, stocks via Yahoo). Used by widgets when
    /// no live feed price is available yet; stores into the shared price map.
    /// </summary>
    public async Task FetchFallbackAsync(string symbol, AssetKind kind)
    {
        if (kind == AssetKind.Crypto)
        {
            string baseCoin = ToFeedKey(symbol, kind);
            if (!CoinGeckoIds.TryGetValue(baseCoin, out string? geckoId)) return;
            string url = $"https://api.coingecko.com/api/v3/simple/price?ids={geckoId}&vs_currencies=usd&include_24hr_change=true";
            string json = await _http.GetStringAsync(url, _cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(geckoId, out var coinEl))
            {
                if (coinEl.TryGetProperty("usd", out var usdEl) && usdEl.ValueKind != JsonValueKind.Null)
                {
                    decimal price = usdEl.GetDecimal();
                    decimal change = coinEl.TryGetProperty("usd_24h_change", out var changeEl) && changeEl.ValueKind != JsonValueKind.Null
                        ? changeEl.GetDecimal()
                        : 0m;
                    _prices[baseCoin] = new PriceInfo
                    {
                        Price = price,
                        ChangePercent = change,
                        Source = "CoinGecko",
                        Timestamp = TimeProvider.System.GetUtcNow().UtcDateTime
                    };
                }
            }
        }
        else if (kind == AssetKind.Stock)
        {
            string stockSym = symbol.ToUpper();
            string url = $"https://query1.finance.yahoo.com/v8/finance/chart/{stockSym}?interval=1d&range=1d";
            string json = await _http.GetStringAsync(url, _cts.Token);
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
            var meta = result.GetProperty("meta");
            decimal price = (decimal)meta.GetProperty("regularMarketPrice").GetDouble();
            decimal prevClose = (decimal)meta.GetProperty("chartPreviousClose").GetDouble();
            decimal changePct = prevClose != 0 ? (price - prevClose) / prevClose * 100m : 0m;
            _prices[stockSym] = new PriceInfo
            {
                Price = price,
                ChangePercent = changePct,
                Source = "Yahoo",
                Timestamp = TimeProvider.System.GetUtcNow().UtcDateTime
            };
        }
    }

    private async Task RunBinanceLoopAsync()
        => await RunWebSocketLoopAsync(
            "wss://stream.binance.us:9443/ws",
            ws => SendJsonAsync(ws, new { method = "SUBSCRIBE", @params = _subscribedCrypto.Keys.Select(c => $"{c.ToLower()}usdt@ticker").ToArray(), id = 1 }),
            ParseBinanceTicker,
            ws => _binanceWs = ws,
            () => _binanceWs = null);

    private async Task RunFinnhubLoopAsync()
    {
        if (string.IsNullOrEmpty(_finnhubKey))
        {
            FileLog.Write("[PRICE-FEED] Finnhub WebSocket feed skipped: FINNHUB_API_KEY not configured.");
            return;
        }

        await RunWebSocketLoopAsync(
            $"wss://ws.finnhub.io?token={_finnhubKey}",
            async ws =>
            {
                foreach (var sym in _subscribedStocks.Keys)
                    await SendJsonAsync(ws, new { type = "subscribe", symbol = sym });
            },
            ParseFinnhubMessage,
            ws => _finnhubWs = ws,
            () => _finnhubWs = null);
    }

    /// <summary>
    /// Shared WebSocket feed loop: connect, subscribe, read until closed, then
    /// reconnect after a delay. The per-feed differences (URI, subscription
    /// payload, message parser) are supplied as delegates.
    /// </summary>
    private async Task RunWebSocketLoopAsync(
        string uri,
        Func<ClientWebSocket, Task> subscribe,
        Action<string> parseMessage,
        Action<ClientWebSocket> onConnected,
        Action onClosed)
    {
        while (!_disposed)
        {
            var ws = new ClientWebSocket();
            try
            {
                onConnected(ws);
                await ws.ConnectAsync(new Uri(uri), _cts.Token);
                await subscribe(ws);
                await ReadLoopAsync(ws, parseMessage, _cts.Token);
            }
            catch when (!_disposed)
            {
                System.Diagnostics.Debug.WriteLine("WebSocket feed loop ended unexpectedly; reconnecting");
            }
            finally
            {
                onClosed();
                ws.Dispose();
            }
            if (!_disposed) await Task.Delay(5000);
        }
    }

    private async Task RunStockRestPollerAsync()
        => await RunRestPollLoopAsync(_stockRestInterval, _subscribedStocks.Keys, PollStockSymbolAsync);

    private async Task PollStockSymbolAsync(string sym)
    {
        var json = await _http.GetStringAsync($"https://finnhub.io/api/v1/quote?symbol={sym}&token={_finnhubKey}", _cts.Token);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("c", out var c) && root.TryGetProperty("dp", out var dp) && dp.ValueKind != JsonValueKind.Null)
        {
            _prices[sym] = new PriceInfo
            {
                Price = c.GetDecimal(),
                ChangePercent = dp.GetDecimal(),
                Source = "Finnhub",
                Timestamp = TimeProvider.System.GetUtcNow().UtcDateTime
            };
        }
    }

    private async Task RunFxRestPollerAsync()
        => await RunRestPollLoopAsync(_stockRestInterval, _subscribedFx.Keys, PollFxPairAsync);

    private async Task PollFxPairAsync(string key)
    {
        if (key.Length != 6)
        {
            return;
        }

        string baseCurrency = key[..3];
        string quoteCurrency = key[3..];
        string start = TimeProvider.System.GetUtcNow().UtcDateTime.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string end = TimeProvider.System.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var json = await _http.GetStringAsync($"https://api.frankfurter.app/{start}..{end}?from={baseCurrency}&to={quoteCurrency}", _cts.Token);
        if (TryParseFrankfurterSeries(json, quoteCurrency, out var price, out var change))
        {
            _prices[key] = new PriceInfo
            {
                Price = price,
                ChangePercent = change,
                Source = "Frankfurter",
                Timestamp = TimeProvider.System.GetUtcNow().UtcDateTime,
                CurrencySymbol = ""
            };
        }
    }

    /// <summary>
    /// Shared REST polling loop: delay, then poll every subscribed symbol via
    /// <paramref name="pollSymbol"/> (individual failures are non-fatal).
    /// </summary>
    private async Task RunRestPollLoopAsync(TimeSpan interval, IEnumerable<string> subscribed, Func<string, Task> pollSymbol)
    {
        while (!_disposed)
        {
            await Task.Delay(interval, _cts.Token);
            foreach (var symbol in subscribed)
            {
                try
                {
                    await pollSymbol(symbol);
                }
                catch
                {
                    // Individual symbol failure is non-fatal.
                    System.Diagnostics.Debug.WriteLine($"REST poll failed for {symbol}; continuing");
                }
            }
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

    private static readonly FrozenDictionary<string, string> CoinGeckoIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["BTC"] = "bitcoin",
        ["ETH"] = "ethereum",
        ["SOL"] = "solana",
        ["DOGE"] = "dogecoin",
        ["ADA"] = "cardano",
        ["XRP"] = "ripple",
        ["DOT"] = "polkadot",
        ["LTC"] = "litecoin",
        ["AVAX"] = "avalanche-2",
        ["LINK"] = "chainlink",
        ["POL"] = "polygon-ecosystem-token",
        ["MATIC"] = "matic-network",
        ["TRX"] = "tron",
        ["SHIB"] = "shiba-inu",
        ["UNI"] = "uniswap",
        ["ATOM"] = "cosmos",
        ["NEAR"] = "near",
        ["APT"] = "aptos",
        ["ARB"] = "arbitrum",
        ["OP"] = "optimism",
        ["SUI"] = "sui",
        ["RNDR"] = "render-token",
        ["FIL"] = "filecoin",
        ["THETA"] = "theta-token",
        ["BNB"] = "binancecoin",
        ["TON"] = "the-open-network",
        ["MNT"] = "mantle",
        ["INJ"] = "injective",
        ["PEPE"] = "pepe",
        ["FLOKI"] = "floki",
        ["BONK"] = "bonk",
        ["HBAR"] = "hedera-hashgraph",
        ["VET"] = "vechain",
        ["AAVE"] = "aave",
        ["MKR"] = "maker",
        ["CRV"] = "curve-dao-token",
        ["EOS"] = "eos",
        ["FET"] = "fetch-ai",
        ["GRT"] = "the-graph",
        ["SEI"] = "sei",
        ["STRK"] = "starknet",
        ["IMX"] = "immutable-x",
        ["DYDX"] = "dydx",
        ["PENDLE"] = "pendle",
        ["KAS"] = "kaspa",
        ["FTM"] = "fantom",
        ["ALGO"] = "algorand",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private async Task RunCryptoRestPollerAsync()
    {
        while (!_disposed)
        {
            await Task.Delay(_cryptoRestInterval, _cts.Token);
            foreach (var sym in _subscribedCrypto.Keys)
            {
                try
                {
                    var json = await _http.GetStringAsync($"https://api.binance.us/api/v3/ticker/24hr?symbol={sym}USDT", _cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("lastPrice", out var lp) && root.TryGetProperty("priceChangePercent", out var pcp))
                    {
                        var priceStr = lp.GetString() ?? "";
                        var changeStr = pcp.GetString() ?? "";
                        if (decimal.TryParse(priceStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) &&
                            decimal.TryParse(changeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var change))
                        {
                            _prices[sym] = new PriceInfo
                            {
                                Price = price,
                                ChangePercent = change,
                                Source = "BinanceUS",
                                Timestamp = TimeProvider.System.GetUtcNow().UtcDateTime
                            };
                        }
                    }
                }
                catch
                {
                    System.Diagnostics.Debug.WriteLine("Crypto REST poll failed for a symbol; continuing");
                }
            }
            await FallbackCoinGeckoAsync();
        }
    }

    private async Task FallbackCoinGeckoAsync()
    {
        try
        {
            var ids = string.Join(",", _subscribedCrypto.Keys.Where(k => CoinGeckoIds.ContainsKey(k)).Select(k => CoinGeckoIds[k]));
            if (string.IsNullOrEmpty(ids)) return;
            var json = await _http.GetStringAsync($"https://api.coingecko.com/api/v3/simple/price?ids={ids}&vs_currencies=usd&include_24hr_change=true", _cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var kvp in CoinGeckoIds)
            {
                if (!root.TryGetProperty(kvp.Value, out var coin)) continue;
                if (!coin.TryGetProperty("usd", out var priceEl) || priceEl.ValueKind == JsonValueKind.Null) continue;
                var price = priceEl.GetDecimal();
                decimal? change = null;
                if (coin.TryGetProperty("usd_24h_change", out var changeEl) && changeEl.ValueKind != JsonValueKind.Null)
                    change = changeEl.GetDecimal();
                _prices.AddOrUpdate(kvp.Key, _ => new PriceInfo
                {
                    Price = price,
                    ChangePercent = change ?? 0,
                    Source = "CoinGecko",
                    Timestamp = TimeProvider.System.GetUtcNow().UtcDateTime
                }, (_, existing) =>
                {
                    if (existing.Source == "BinanceUS" && (TimeProvider.System.GetUtcNow().UtcDateTime - existing.Timestamp).TotalSeconds < 60)
                        return existing;
                    return new PriceInfo
                    {
                        Price = price,
                        ChangePercent = change ?? existing.ChangePercent,
                        Source = "CoinGecko",
                        Timestamp = TimeProvider.System.GetUtcNow().UtcDateTime
                    };
                });
            }
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("CoinGecko fallback price fetch failed; continuing");
        }
    }

    private void ParseBinanceTicker(string json)
    {
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
            else return;

            if (s.EndsWith("USDT") && decimal.TryParse(c, NumberStyles.Any, CultureInfo.InvariantCulture, out var price) && decimal.TryParse(P, NumberStyles.Any, CultureInfo.InvariantCulture, out var change))
            {
                var coin = s[..^4].ToUpper();
                _prices[coin] = new PriceInfo
                {
                    Price = price,
                    ChangePercent = change,
                    Source = "Binance",
                    Timestamp = TimeProvider.System.GetUtcNow().UtcDateTime
                };
            }
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("Failed to parse Binance ticker message; ignoring");
        }
    }

    private void ParseFinnhubMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            if (type == "trade" && root.TryGetProperty("data", out var trades))
            {
                foreach (var trade in trades.EnumerateArray())
                {
                    var s = trade.GetProperty("s").GetString() ?? "";
                    var p = trade.GetProperty("p").GetDecimal();
                    _prices.AddOrUpdate(s, _ => new PriceInfo { Price = p, Source = "Finnhub", Timestamp = TimeProvider.System.GetUtcNow().UtcDateTime },
                        (_, existing) => new PriceInfo { Price = p, ChangePercent = existing.ChangePercent, Source = "Finnhub", Timestamp = TimeProvider.System.GetUtcNow().UtcDateTime });
                }
            }
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("Failed to parse Finnhub message; ignoring");
        }
    }

    private static async Task ReadLoopAsync(ClientWebSocket ws, Action<string> handler, CancellationToken ct)
    {
        var buffer = new byte[16384];
        var fragment = new StringBuilder();
        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (result.MessageType == WebSocketMessageType.Text)
            {
                fragment.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                {
                    handler(fragment.ToString());
                    fragment.Clear();
                }
            }
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket ws, object obj)
    {
        var json = JsonSerializer.Serialize(obj);
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        _binanceWs?.Dispose();
        _finnhubWs?.Dispose();
        _http.Dispose();
    }
}
