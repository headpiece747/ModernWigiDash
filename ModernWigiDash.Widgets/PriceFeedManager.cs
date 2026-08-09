using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Globalization;
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
    /// <summary>Canonical base coin for a crypto alias plus its CoinGecko API id.</summary>
    internal sealed record CryptoAlias(string Symbol, string CoinGeckoId);

    internal enum WebSocketFeed
    {
        Binance,
        Finnhub
    }

    /// <summary>
    /// One crypto symbol table: user-facing alias â†’ canonical base coin + the
    /// CoinGecko API id used by the REST fallback. A single table makes a
    /// symbol with a working live feed but a missing fallback id
    /// unrepresentable — the fallback can never silently lose a coin.
    /// </summary>
    internal static readonly FrozenDictionary<string, CryptoAlias> CryptoAliases =
        new Dictionary<string, CryptoAlias>(StringComparer.OrdinalIgnoreCase)
        {
            ["bitcoin"] = new("BTC", "bitcoin"),
            ["btc"] = new("BTC", "bitcoin"),
            ["ethereum"] = new("ETH", "ethereum"),
            ["eth"] = new("ETH", "ethereum"),
            ["solana"] = new("SOL", "solana"),
            ["sol"] = new("SOL", "solana"),
            ["dogecoin"] = new("DOGE", "dogecoin"),
            ["doge"] = new("DOGE", "dogecoin"),
            ["cardano"] = new("ADA", "cardano"),
            ["ada"] = new("ADA", "cardano"),
            ["ripple"] = new("XRP", "ripple"),
            ["xrp"] = new("XRP", "ripple"),
            ["polkadot"] = new("DOT", "polkadot"),
            ["dot"] = new("DOT", "polkadot"),
            ["litecoin"] = new("LTC", "litecoin"),
            ["ltc"] = new("LTC", "litecoin"),
            ["avalanche-2"] = new("AVAX", "avalanche-2"),
            ["avax"] = new("AVAX", "avalanche-2"),
            ["chainlink"] = new("LINK", "chainlink"),
            ["link"] = new("LINK", "chainlink"),
            ["polygon"] = new("POL", "polygon-ecosystem-token"),
            ["pol"] = new("POL", "polygon-ecosystem-token"),
            ["matic-network"] = new("MATIC", "matic-network"),
            ["matic"] = new("MATIC", "matic-network"),
            ["tron"] = new("TRX", "tron"),
            ["trx"] = new("TRX", "tron"),
            ["shiba-inu"] = new("SHIB", "shiba-inu"),
            ["shib"] = new("SHIB", "shiba-inu"),
            ["uniswap"] = new("UNI", "uniswap"),
            ["uni"] = new("UNI", "uniswap"),
            ["cosmos"] = new("ATOM", "cosmos"),
            ["atom"] = new("ATOM", "cosmos"),
            ["near"] = new("NEAR", "near"),
            ["aptos"] = new("APT", "aptos"),
            ["apt"] = new("APT", "aptos"),
            ["arbitrum"] = new("ARB", "arbitrum"),
            ["arb"] = new("ARB", "arbitrum"),
            ["optimism"] = new("OP", "optimism"),
            ["op"] = new("OP", "optimism"),
            ["sui"] = new("SUI", "sui"),
            ["render"] = new("RNDR", "render-token"),
            ["rndr"] = new("RNDR", "render-token"),
            ["filecoin"] = new("FIL", "filecoin"),
            ["fil"] = new("FIL", "filecoin"),
            ["theta"] = new("THETA", "theta-token"),
            ["bnb"] = new("BNB", "binancecoin"),
            ["toncoin"] = new("TON", "the-open-network"),
            ["ton"] = new("TON", "the-open-network"),
            ["mantle"] = new("MNT", "mantle"),
            ["mnt"] = new("MNT", "mantle"),
            ["injective"] = new("INJ", "injective"),
            ["inj"] = new("INJ", "injective"),
            ["pepe"] = new("PEPE", "pepe"),
            ["floki"] = new("FLOKI", "floki"),
            ["bonk"] = new("BONK", "bonk"),
            ["hedera"] = new("HBAR", "hedera-hashgraph"),
            ["hbar"] = new("HBAR", "hedera-hashgraph"),
            ["vechain"] = new("VET", "vechain"),
            ["vet"] = new("VET", "vechain"),
            ["aave"] = new("AAVE", "aave"),
            ["maker"] = new("MKR", "maker"),
            ["mkr"] = new("MKR", "maker"),
            ["curve"] = new("CRV", "curve-dao-token"),
            ["crv"] = new("CRV", "curve-dao-token"),
            ["eos"] = new("EOS", "eos"),
            ["fetch"] = new("FET", "fetch-ai"),
            ["fet"] = new("FET", "fetch-ai"),
            ["fetch-ai"] = new("FET", "fetch-ai"),
            ["the-graph"] = new("GRT", "the-graph"),
            ["grt"] = new("GRT", "the-graph"),
            ["sei"] = new("SEI", "sei"),
            ["starknet"] = new("STRK", "starknet"),
            ["strk"] = new("STRK", "starknet"),
            ["immutable"] = new("IMX", "immutable-x"),
            ["imx"] = new("IMX", "immutable-x"),
            ["dydx"] = new("DYDX", "dydx"),
            ["pendle"] = new("PENDLE", "pendle"),
            ["kaspa"] = new("KAS", "kaspa"),
            ["kas"] = new("KAS", "kaspa"),
            ["fantom"] = new("FTM", "fantom"),
            ["ftm"] = new("FTM", "fantom"),
            ["algorand"] = new("ALGO", "algorand"),
            ["algo"] = new("ALGO", "algorand"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> KnownCryptos = CryptoAliases.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the CoinGecko API id for a canonical base coin, or null when unknown.</summary>
    private static string? CoinGeckoIdFor(string baseCoin)
        => CryptoAliases.TryGetValue(baseCoin, out var alias) ? alias.CoinGeckoId : null;

    private readonly string _finnhubKey;
    private readonly ConcurrentDictionary<string, PriceInfo> _prices = new();
    internal readonly ConcurrentDictionary<string, byte> _subscribedCrypto = new();
    internal readonly ConcurrentDictionary<string, byte> _subscribedStocks = new();
    internal readonly ConcurrentDictionary<string, byte> _subscribedFx = new();
    private readonly HttpClient _http;
    private readonly TimeSpan _stockRestInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _cryptoRestInterval = TimeSpan.FromSeconds(30);

    private IWebSocketFeed? _binanceFeed;
    private IWebSocketFeed? _finnhubFeed;
    private readonly Func<WebSocketFeed, IWebSocketFeed> _feedFactory;
    private readonly TimeSpan _reconnectDelay;
    private CancellationTokenSource _cts = new();
    private Task? _binanceTask;
    private Task? _finnhubTask;
    private Task? _stockRestTask;
    private Task? _cryptoRestTask;
    private Task? _fxRestTask;
    private bool _disposed;

    /// <summary>
    /// One long-lived client shared by every feed manager. Widgets are
    /// reflection-instantiated (parameterless ctor) so no DI/IHttpClientFactory
    /// is available — a shared client reuses sockets instead of creating one
    /// HttpClient per instance.
    /// </summary>
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    private static HttpClient CreateSharedHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ModernWigiDash/2.0");
        return client;
    }

    public PriceFeedManager(string? finnhubApiKey = null)
        : this(SharedHttpClient, finnhubApiKey)
    {
    }

    /// <summary>Internal constructor with injectable seams: HttpClient, WebSocket feed factory, reconnect delay.</summary>
    internal PriceFeedManager(
        HttpClient httpClient,
        string? finnhubApiKey = null,
        Func<WebSocketFeed, IWebSocketFeed>? feedFactory = null,
        TimeSpan? reconnectDelay = null)
    {
        _http = httpClient;
        _feedFactory = feedFactory ?? (_ => new ClientWebSocketFeed());
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(5);
        // The Finnhub key must come from an explicit argument or the
        // FINNHUB_API_KEY environment variable — never from source control.
        _finnhubKey = finnhubApiKey ?? Environment.GetEnvironmentVariable("FINNHUB_API_KEY") ?? "";
        if (string.IsNullOrEmpty(_finnhubKey))
        {
            FileLog.Write("[PRICE-FEED] FINNHUB_API_KEY not configured — stock WebSocket/REST feeds disabled. Set the FINNHUB_API_KEY environment variable or pass the key to the constructor. Yahoo Finance fallback still works.");
        }
        // Idempotent across instances that share a client (the static default).
        _http.DefaultRequestHeaders.UserAgent.TryParseAdd("ModernWigiDash/2.0");
    }

    /// <summary>
    /// Accepts only well-formed ticker/pair symbols (ASCII letters, digits, and
    /// '.', '-', ':' separators, up to 32 chars) so user-typed input can never
    /// pollute feed URLs or subscription payloads.
    /// </summary>
    private static bool IsValidSymbol(string symbol) =>
        symbol.Length is > 0 and <= 32 && symbol.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or ':');

    /// <summary>FX pair keys are exactly six letters (e.g. "EURUSD").</summary>
    private static bool IsValidFxKey(string key) =>
        key.Length == 6 && key.All(char.IsAsciiLetter);

    /// <summary>
    /// Validates an FX subscription input: either the "XXX/YYY" pair form (both
    /// halves and the shape checked by <see cref="TryParseFxPair"/>) or a bare
    /// six-letter key. Returns the normalized key when valid.
    /// </summary>
    private static bool IsValidFxInput(string symbol, out string fxKey)
    {
        fxKey = "";
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        if (symbol.Contains('/'))
        {
            if (!TryParseFxPair(symbol, out _, out _)) return false;
        }
        else if (!IsValidSymbol(symbol))
        {
            return false;
        }
        fxKey = NormalizeFxKey(symbol);
        return IsValidFxKey(fxKey);
    }

    private static void LogInvalidSymbol(string? symbol)
    {
        string preview;
        if (symbol is null) preview = "<null>";
        else if (symbol.Length > 48) preview = symbol[..48] + "…";
        else preview = symbol;
        System.Diagnostics.Debug.WriteLine($"Skipping invalid feed symbol '{preview}'");
    }

    public static bool IsCrypto(string symbol) => KnownCryptos.Contains(symbol);
    public static string NormalizeSymbol(string symbol) =>
        CryptoAliases.TryGetValue(symbol, out var alias) ? alias.Symbol : symbol.ToUpper();

    /// <summary>
    /// Maps a user-entered symbol to the canonical feed key for an asset kind:
    /// crypto aliases resolve to the base coin (e.g. "bitcoin" â†’ "BTC"), FX
    /// pairs to "EURUSD", everything else to the upper-cased symbol.
    /// </summary>
    public static string ToFeedKey(string symbol, AssetKind kind) => kind switch
    {
        AssetKind.Crypto => CryptoAliases.TryGetValue(symbol, out var alias) ? alias.Symbol : symbol.ToUpper(),
        AssetKind.Fx => NormalizeFxKey(symbol),
        _ => symbol.ToUpper()
    };

    // Timeout guards the match against catastrophic backtracking on hostile input.
    private static readonly Regex FxPairRegex = new("^([A-Za-z]{3})/([A-Za-z]{3})$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

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
        EnsureActive();
        switch (kind)
        {
            case AssetKind.Crypto:
                if (!IsValidSymbol(symbol))
                {
                    LogInvalidSymbol(symbol);
                    return;
                }
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
                if (!IsValidFxInput(symbol, out string fxKey))
                {
                    LogInvalidSymbol(symbol);
                    return;
                }
                if (_subscribedFx.TryAdd(fxKey, 0))
                {
                    _fxRestTask ??= RunFxRestPollerAsync();
                }
                break;
            default:
                if (!IsValidSymbol(symbol))
                {
                    LogInvalidSymbol(symbol);
                    return;
                }
                string stockSym = symbol.ToUpper();
                // Without a Finnhub key the WS/REST stock feeds cannot work; the
                // Yahoo Finance fallback in FetchFallbackAsync still does.
                if (_subscribedStocks.TryAdd(stockSym, 0) && !string.IsNullOrEmpty(_finnhubKey))
                {
                    _finnhubTask ??= RunFinnhubLoopAsync();
                    _stockRestTask ??= RunStockRestPollerAsync();
                    _ = SendWsSubscribeAsync(WebSocketFeed.Finnhub, stockSym);
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
            IWebSocketFeed? ws = feed == WebSocketFeed.Finnhub ? _finnhubFeed : _binanceFeed;
            if (ws == null || !ws.IsOpen) return;
            object message = feed == WebSocketFeed.Finnhub
                ? new { type = "subscribe", symbol = payload }
                : new { method = "SUBSCRIBE", @params = new[] { payload }, id = 1 };
            await ws.SendTextAsync(JsonSerializer.Serialize(message), _cts.Token);
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
        string key = ToFeedKey(symbol, kind);
        switch (kind)
        {
            case AssetKind.Crypto:
                _subscribedCrypto.TryRemove(key, out _);
                break;
            case AssetKind.Fx:
                _subscribedFx.TryRemove(key, out _);
                break;
            default:
                _subscribedStocks.TryRemove(key, out _);
                break;
        }

        // Prices for an unsubscribed symbol are stale by construction.
        _prices.TryRemove(key, out _);

        // Ref-counted shutdown: when the last subscriber leaves, stop the
        // sockets and pollers so the static per-widget feed does not hold
        // process-lifetime network handles.
        if (_subscribedCrypto.IsEmpty && _subscribedStocks.IsEmpty && _subscribedFx.IsEmpty)
        {
            ShutdownLoops();
        }
    }

    /// <summary>
    /// Restarts a cancelled feed lifecycle (called before subscribing again
    /// after the last subscriber previously triggered shutdown).
    /// </summary>
    private void EnsureActive()
    {
        if (_cts.IsCancellationRequested)
        {
            var replacement = new CancellationTokenSource();
            Interlocked.Exchange(ref _cts, replacement);
        }
    }

    /// <summary>Cancels the feed loops and closes the sockets when no subscribers remain.</summary>
    private void ShutdownLoops()
    {
        _cts.Cancel();
        try { _binanceFeed?.Abort(); } catch { /* best-effort */ }
        try { _finnhubFeed?.Abort(); } catch { /* best-effort */ }
        _binanceFeed = null;
        _finnhubFeed = null;
        _binanceTask = null;
        _finnhubTask = null;
        _stockRestTask = null;
        _cryptoRestTask = null;
        _fxRestTask = null;
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
            if (CoinGeckoIdFor(baseCoin) is not string geckoId) return;
            string url = $"https://api.coingecko.com/api/v3/simple/price?ids={geckoId}&vs_currencies=usd&include_24hr_change=true";
            string json = await _http.GetStringAsync(url, _cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(geckoId, out var coinEl)
                && coinEl.TryGetProperty("usd", out var usdEl)
                && usdEl.ValueKind != JsonValueKind.Null)
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
        else if (kind == AssetKind.Stock)
        {
            string stockSym = symbol.ToUpper();
            if (!IsValidSymbol(stockSym)) return;
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
            WebSocketFeed.Binance,
            "wss://stream.binance.us:9443/ws",
            feed => feed.SendTextAsync(JsonSerializer.Serialize(new { method = "SUBSCRIBE", @params = _subscribedCrypto.Keys.Select(c => $"{c.ToLower()}usdt@ticker").ToArray(), id = 1 }), _cts.Token),
            ParseBinanceTicker,
            feed => _binanceFeed = feed,
            () => _binanceFeed = null);

    private async Task RunFinnhubLoopAsync()
    {
        if (string.IsNullOrEmpty(_finnhubKey))
        {
            FileLog.Write("[PRICE-FEED] Finnhub WebSocket feed skipped: FINNHUB_API_KEY not configured.");
            return;
        }

        await RunWebSocketLoopAsync(
            WebSocketFeed.Finnhub,
            $"wss://ws.finnhub.io?token={_finnhubKey}",
            async feed =>
            {
                foreach (var sym in _subscribedStocks.Keys)
                    await feed.SendTextAsync(JsonSerializer.Serialize(new { type = "subscribe", symbol = sym }), _cts.Token);
            },
            ParseFinnhubMessage,
            feed => _finnhubFeed = feed,
            () => _finnhubFeed = null);
    }

    /// <summary>
    /// Shared WebSocket feed loop: connect, subscribe, read until closed, then
    /// reconnect after a delay. The per-feed differences (URI, subscription
    /// payload, message parser) are supplied as delegates; the socket itself
    /// comes from the feed factory seam, so tests drive the loop with an
    /// in-memory feed.
    /// </summary>
    private async Task RunWebSocketLoopAsync(
        WebSocketFeed feedKind,
        string uri,
        Func<IWebSocketFeed, Task> subscribe,
        Action<string> parseMessage,
        Action<IWebSocketFeed> onConnected,
        Action onClosed)
    {
        while (!_disposed && !_cts.IsCancellationRequested)
        {
            IWebSocketFeed feed = _feedFactory(feedKind);
            try
            {
                onConnected(feed);
                await feed.ConnectAsync(new Uri(uri), _cts.Token);
                await subscribe(feed);
                string? message;
                while ((message = await feed.ReceiveTextAsync(_cts.Token)) is not null)
                {
                    parseMessage(message);
                }
            }
            catch when (!_disposed && !_cts.IsCancellationRequested)
            {
                System.Diagnostics.Debug.WriteLine("WebSocket feed loop ended unexpectedly; reconnecting");
            }
            finally
            {
                onClosed();
                feed.Dispose();
            }
            if (!_disposed && !_cts.IsCancellationRequested) await Task.Delay(_reconnectDelay, _cts.Token);
        }
    }

    private async Task RunStockRestPollerAsync()
        => await RunRestPollLoopAsync(_stockRestInterval, _subscribedStocks.Keys, PollStockSymbolAsync);

    private async Task PollStockSymbolAsync(string sym)
    {
        if (!IsValidSymbol(sym)) return;
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
        if (!IsValidFxKey(key))
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
            var ids = string.Join(",", _subscribedCrypto.Keys.Select(CoinGeckoIdFor).OfType<string>());
            if (string.IsNullOrEmpty(ids)) return;
            var json = await _http.GetStringAsync($"https://api.coingecko.com/api/v3/simple/price?ids={ids}&vs_currencies=usd&include_24hr_change=true", _cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var alias in CryptoAliases.Values.DistinctBy(a => a.Symbol))
            {
                if (!root.TryGetProperty(alias.CoinGeckoId, out var coin)) continue;
                if (!coin.TryGetProperty("usd", out var priceEl) || priceEl.ValueKind == JsonValueKind.Null) continue;
                var price = priceEl.GetDecimal();
                decimal? change = null;
                if (coin.TryGetProperty("usd_24h_change", out var changeEl) && changeEl.ValueKind != JsonValueKind.Null)
                    change = changeEl.GetDecimal();
                _prices.AddOrUpdate(alias.Symbol, _ => new PriceInfo
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        try { _binanceFeed?.Abort(); } catch { /* best-effort */ }
        try { _finnhubFeed?.Abort(); } catch { /* best-effort */ }
        _binanceFeed?.Dispose();
        _finnhubFeed?.Dispose();
        // The manager never owns its HttpClient: the default instance shares
        // the static process-wide client, so disposing it here would kill every
        // other feed manager's socket reuse (the latent cross-widget break).
        // The client lives for the process; only the loops are shut down.
    }
}
