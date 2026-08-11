using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
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
    public string FormattedChange => $"{(ChangePercent >= 0 ? "+" : "")}{ChangePercent:F2}%";
    public bool IsPositive => ChangePercent >= 0;
    public bool IsStale => (Clock.GetUtcNow().UtcDateTime - Timestamp).TotalSeconds > 60;

    /// <summary>Test seam: clock for the staleness decision.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
}

public sealed class PriceFeedManager : IDisposable
{
    internal enum FeedKind
    {
        Binance,
        Finnhub
    }

    private readonly string _finnhubKey;
    private readonly ConcurrentDictionary<string, PriceInfo> _prices = new();
    // Subscriber claim counts: N widgets on one symbol hold N claims, so one
    // widget's unsubscribe only releases when the last claim leaves.
    internal readonly ConcurrentDictionary<string, int> _subscribedCrypto = new();
    internal readonly ConcurrentDictionary<string, int> _subscribedStocks = new();
    internal readonly ConcurrentDictionary<string, int> _subscribedFx = new();
    private readonly HttpClient _http;
    private readonly TimeSpan _stockRestInterval = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _cryptoRestInterval = TimeSpan.FromSeconds(30);

    private readonly Func<FeedKind, IWebSocketFeed> _feedFactory;
    private readonly TimeSpan _reconnectDelay;
    private CancellationTokenSource _cts = new();
    private FeedLoop? _binanceLoop;
    private FeedLoop? _finnhubLoop;
    private Task? _stockRestTask;
    private Task? _cryptoRestTask;
    private Task? _fxRestTask;
    private bool _disposed;

    /// <summary>Test seam: injectable clock for price timestamps and staleness.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

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
        Func<FeedKind, IWebSocketFeed>? feedFactory = null,
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

    public void Subscribe(string symbol, AssetKind kind)
    {
        EnsureActive();
        switch (kind)
        {
            case AssetKind.Crypto:
                if (!SymbolCatalog.IsValidSymbol(symbol))
                {
                    SymbolCatalog.LogInvalidSymbol(symbol);
                    return;
                }
                var baseCoin = SymbolCatalog.ToFeedKey(symbol, kind);
                // Ref-counted: the shared manager keys subscriptions by symbol,
                // so N widgets on one symbol hold N claims — one widget's
                // symbol change must not kill another's live feed.
                if (_subscribedCrypto.AddOrUpdate(baseCoin, 1, (_, count) => count + 1) == 1)
                {
                    _binanceLoop ??= CreateBinanceLoop();
                    _binanceLoop.Start();
                    _cryptoRestTask ??= RunCryptoRestPollerAsync();
                    // Push an incremental subscribe so symbols added after the
                    // socket connected still receive real-time ticks.
                    _ = SendWsSubscribeAsync(FeedKind.Binance, $"{baseCoin.ToLower()}usdt@ticker");
                }
                break;
            case AssetKind.Fx:
                if (!SymbolCatalog.IsValidFxInput(symbol, out string fxKey))
                {
                    SymbolCatalog.LogInvalidSymbol(symbol);
                    return;
                }
                if (_subscribedFx.AddOrUpdate(fxKey, 1, (_, count) => count + 1) == 1)
                {
                    _fxRestTask ??= RunFxRestPollerAsync();
                }
                break;
            default:
                if (!SymbolCatalog.IsValidSymbol(symbol))
                {
                    SymbolCatalog.LogInvalidSymbol(symbol);
                    return;
                }
                string stockSym = symbol.ToUpper();
                // Without a Finnhub key the WS/REST stock feeds cannot work; the
                // Yahoo Finance fallback in FetchFallbackAsync still does.
                if (_subscribedStocks.AddOrUpdate(stockSym, 1, (_, count) => count + 1) == 1
                    && !string.IsNullOrEmpty(_finnhubKey))
                {
                    _finnhubLoop ??= CreateFinnhubLoop();
                    _finnhubLoop.Start();
                    _stockRestTask ??= RunStockRestPollerAsync();
                    _ = SendWsSubscribeAsync(FeedKind.Finnhub, stockSym);
                }
                break;
        }
    }

    /// <summary>
    /// Sends an incremental WebSocket subscription for a symbol added after the
    /// feed socket was already connected. No-op when the socket is not open.
    /// </summary>
    private async Task SendWsSubscribeAsync(FeedKind feed, string payload)
    {
        try
        {
            IWebSocketFeed? ws = feed == FeedKind.Finnhub ? _finnhubLoop?.Current : _binanceLoop?.Current;
            if (ws == null || !ws.IsOpen) return;
            object message = feed == FeedKind.Finnhub
                ? new { type = "subscribe", symbol = payload }
                : new { method = "SUBSCRIBE", @params = new[] { payload }, id = 1 };
            await ws.SendTextAsync(JsonSerializer.Serialize(message), _cts.Token);
        }
        catch
        {
            // Incremental subscribe is best-effort; the connect-time payload
            // covers the symbols known at that point.
            FileLog.Write($"[PRICE-FEED] Incremental feed subscribe failed for {payload}");
        }
    }

    /// <summary>
    /// Stops polling for <paramref name="symbol"/> (e.g. after the widget was
    /// removed from the canvas). Ref-counted: only the last subscriber removes
    /// the key, so one widget's unsubscribe never kills another widget's live
    /// feed on the same symbol. The underlying loops keep running while any
    /// symbol remains subscribed, and stop entirely when the last one leaves.
    /// </summary>
    public void Unsubscribe(string symbol, AssetKind kind)
    {
        string key = SymbolCatalog.ToFeedKey(symbol, kind);
        bool fullyReleased = kind switch
        {
            AssetKind.Crypto => ReleaseSubscription(_subscribedCrypto, key),
            AssetKind.Fx => ReleaseSubscription(_subscribedFx, key),
            _ => ReleaseSubscription(_subscribedStocks, key),
        };

        // Prices for a fully-released symbol are stale by construction; a
        // symbol with remaining subscribers keeps its cached price.
        if (fullyReleased)
        {
            _prices.TryRemove(key, out _);
        }

        // Ref-counted shutdown: when the last subscriber leaves, stop the
        // sockets and pollers so the static per-widget feed does not hold
        // process-lifetime network handles.
        if (_subscribedCrypto.IsEmpty && _subscribedStocks.IsEmpty && _subscribedFx.IsEmpty)
        {
            ShutdownLoops();
        }
    }

    /// <summary>Releases one subscriber claim; true when the LAST claim was
    /// released (the key is removed) — a symbol with remaining subscribers
    /// keeps its key and cached price.</summary>
    private static bool ReleaseSubscription(ConcurrentDictionary<string, int> subscriptions, string key)
    {
        if (!subscriptions.TryGetValue(key, out int count)) return false;
        if (count <= 1) return subscriptions.TryRemove(key, out _);
        subscriptions.AddOrUpdate(key, count - 1, (_, current) => current - 1);
        return false;
    }

    /// <summary>
    /// Restarts a cancelled feed lifecycle (called before subscribing again
    /// after the last subscriber previously triggered shutdown).
    /// </summary>
    private void EnsureActive()
    {
        // A disposed manager must never re-arm its loops (latent: profile
        // dispose precedes manager lifetime today, but the guard makes the
        // invariant structural).
        if (_disposed) return;
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
        _binanceLoop?.Dispose();
        _finnhubLoop?.Dispose();
        _binanceLoop = null;
        _finnhubLoop = null;
        _stockRestTask = null;
        _cryptoRestTask = null;
        _fxRestTask = null;
    }

    public PriceInfo? GetPrice(string symbol, AssetKind kind)
    {
        string key = SymbolCatalog.ToFeedKey(symbol, kind);
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
            string baseCoin = SymbolCatalog.ToFeedKey(symbol, kind);
            if (SymbolCatalog.CoinGeckoIdFor(baseCoin) is not string geckoId) return;
            string url = $"https://api.coingecko.com/api/v3/simple/price?ids={geckoId}&vs_currencies=usd&include_24hr_change=true";
            string json = await _http.GetStringAsync(url, _cts.Token);
            if (PriceFeedMessages.TryParseCoinGeckoSimplePrice(json, geckoId, out var price, out var change))
            {
                _prices[baseCoin] = new PriceInfo
                {
                    Price = price,
                    ChangePercent = change ?? 0m,
                    Source = "CoinGecko",
                    Timestamp = Clock.GetUtcNow().UtcDateTime
                };
            }
        }
        else if (kind == AssetKind.Stock)
        {
            string stockSym = symbol.ToUpper();
            if (!SymbolCatalog.IsValidSymbol(stockSym)) return;
            string url = $"https://query1.finance.yahoo.com/v8/finance/chart/{stockSym}?interval=1d&range=1d";
            string json = await _http.GetStringAsync(url, _cts.Token);
            if (PriceFeedMessages.TryParseYahooChart(json, out var price, out var changePct))
            {
                _prices[stockSym] = new PriceInfo
                {
                    Price = price,
                    ChangePercent = changePct,
                    Source = "Yahoo",
                    Timestamp = Clock.GetUtcNow().UtcDateTime
                };
            }
        }
    }

    private FeedLoop CreateBinanceLoop() => new(
        new Uri("wss://stream.binance.us:9443/ws"),
        () => _feedFactory(FeedKind.Binance),
        (feed, ct) => feed.SendTextAsync(JsonSerializer.Serialize(new { method = "SUBSCRIBE", @params = _subscribedCrypto.Keys.Select(c => $"{c.ToLower()}usdt@ticker").ToArray(), id = 1 }), ct),
        ParseBinanceTicker,
        new FixedReconnectPolicy(_reconnectDelay));

    private FeedLoop CreateFinnhubLoop() => new(
        new Uri($"wss://ws.finnhub.io?token={_finnhubKey}"),
        () => _feedFactory(FeedKind.Finnhub),
        async (feed, ct) =>
        {
            foreach (var sym in _subscribedStocks.Keys)
                await feed.SendTextAsync(JsonSerializer.Serialize(new { type = "subscribe", symbol = sym }), ct);
        },
        ParseFinnhubMessage,
        new FixedReconnectPolicy(_reconnectDelay));

    private async Task RunStockRestPollerAsync()
        => await RunRestPollLoopAsync(_stockRestInterval, _subscribedStocks.Keys, PollStockSymbolAsync);

    internal async Task PollStockSymbolAsync(string sym)
    {
        if (!SymbolCatalog.IsValidSymbol(sym)) return;
        var json = await _http.GetStringAsync($"https://finnhub.io/api/v1/quote?symbol={sym}&token={_finnhubKey}", _cts.Token);
        if (PriceFeedMessages.TryParseFinnhubQuote(json, out var price, out var change))
        {
            _prices[sym] = new PriceInfo
            {
                Price = price,
                ChangePercent = change ?? 0m,
                Source = "Finnhub",
                Timestamp = Clock.GetUtcNow().UtcDateTime
            };
        }
    }

    private async Task RunFxRestPollerAsync()
        => await RunRestPollLoopAsync(_stockRestInterval, _subscribedFx.Keys, PollFxPairAsync);

    internal async Task PollFxPairAsync(string key)
    {
        if (!SymbolCatalog.IsValidFxKey(key))
        {
            return;
        }

        string baseCurrency = key[..3];
        string quoteCurrency = key[3..];
        string start = Clock.GetUtcNow().UtcDateTime.AddDays(-10).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string end = Clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var json = await _http.GetStringAsync($"https://api.frankfurter.app/{start}..{end}?from={baseCurrency}&to={quoteCurrency}", _cts.Token);
        if (PriceFeedMessages.TryParseFrankfurterSeries(json, quoteCurrency, out var price, out var change))
        {
            _prices[key] = new PriceInfo
            {
                Price = price,
                ChangePercent = change,
                Source = "Frankfurter",
                Timestamp = Clock.GetUtcNow().UtcDateTime,
                CurrencySymbol = ""
            };
        }
    }

    /// <summary>
    /// Shared REST polling loop: delay, then poll every subscribed symbol via
    /// <paramref name="pollSymbol"/> (individual failures are non-fatal),
    /// then run the optional <paramref name="afterBatch"/> action at the same
    /// point of the cycle (e.g. the crypto loop's CoinGecko fallback).
    /// </summary>
    private async Task RunRestPollLoopAsync(TimeSpan interval, IEnumerable<string> subscribed, Func<string, Task> pollSymbol, Func<Task>? afterBatch = null)
    {
        while (!_disposed)
        {
            try
            {
                await Task.Delay(interval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Shutdown: end the loop normally instead of faulting the
                // stored task (unobserved task faults on dispose).
                break;
            }
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
            if (afterBatch is not null)
            {
                await afterBatch();
            }
        }
    }

    private async Task RunCryptoRestPollerAsync()
        => await RunRestPollLoopAsync(_cryptoRestInterval, _subscribedCrypto.Keys, PollCryptoSymbolAsync, FallbackCoinGeckoAsync);

    /// <summary>
    /// One crypto REST poll hop: the BinanceUS 24hr ticker for one subscribed
    /// symbol. The loop owns the per-symbol failure isolation (see
    /// <see cref="RunRestPollLoopAsync"/>); this is fetch → parse → store only.
    /// </summary>
    internal async Task PollCryptoSymbolAsync(string sym)
    {
        var json = await _http.GetStringAsync($"https://api.binance.us/api/v3/ticker/24hr?symbol={sym}USDT", _cts.Token);
        if (PriceFeedMessages.TryParseBinanceRestTicker(json, out var price, out var change))
        {
            _prices[sym] = new PriceInfo
            {
                Price = price,
                ChangePercent = change,
                Source = "BinanceUS",
                Timestamp = Clock.GetUtcNow().UtcDateTime
            };
        }
    }

    internal async Task FallbackCoinGeckoAsync()
    {
        try
        {
            var ids = string.Join(",", _subscribedCrypto.Keys.Select(SymbolCatalog.CoinGeckoIdFor).OfType<string>());
            if (string.IsNullOrEmpty(ids)) return;
            var json = await _http.GetStringAsync($"https://api.coingecko.com/api/v3/simple/price?ids={ids}&vs_currencies=usd&include_24hr_change=true", _cts.Token);
            foreach (var alias in SymbolCatalog.CryptoAliases.Values.DistinctBy(a => a.Symbol))
            {
                if (!PriceFeedMessages.TryParseCoinGeckoSimplePrice(json, alias.CoinGeckoId, out var price, out var change))
                {
                    continue;
                }
                _prices.AddOrUpdate(alias.Symbol, _ => new PriceInfo
                {
                    Price = price,
                    ChangePercent = change ?? 0,
                    Source = "CoinGecko",
                    Timestamp = Clock.GetUtcNow().UtcDateTime
                }, (_, existing) =>
                {
                    if (existing.Source == "BinanceUS" && (Clock.GetUtcNow().UtcDateTime - existing.Timestamp).TotalSeconds < 60)
                        return existing;
                    return new PriceInfo
                    {
                        Price = price,
                        ChangePercent = change ?? existing.ChangePercent,
                        Source = "CoinGecko",
                        Timestamp = Clock.GetUtcNow().UtcDateTime
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
        if (!PriceFeedMessages.TryParseBinanceTicker(json, out var coin, out var price, out var change))
        {
            System.Diagnostics.Debug.WriteLine("Failed to parse Binance ticker message; ignoring");
            return;
        }
        _prices[coin] = new PriceInfo
        {
            Price = price,
            ChangePercent = change,
            Source = "Binance",
            Timestamp = Clock.GetUtcNow().UtcDateTime
        };
    }

    private void ParseFinnhubMessage(string json)
    {
        if (!PriceFeedMessages.TryParseFinnhubTrades(json, out var trades))
        {
            System.Diagnostics.Debug.WriteLine("Failed to parse Finnhub message; ignoring");
            return;
        }
        foreach (var trade in trades)
        {
            _prices.AddOrUpdate(trade.Symbol, _ => new PriceInfo { Price = trade.Price, Source = "Finnhub", Timestamp = Clock.GetUtcNow().UtcDateTime },
                (_, existing) => new PriceInfo { Price = trade.Price, ChangePercent = existing.ChangePercent, Source = "Finnhub", Timestamp = Clock.GetUtcNow().UtcDateTime });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        // Deliberately NOT disposed here: fire-and-forget sends may still be
        // awaiting with this token (the loops break on OCE and never re-touch
        // it); the codebase's deferral pattern lets the source be GC'd.
        _binanceLoop?.Dispose();
        _finnhubLoop?.Dispose();
        // The manager never owns its HttpClient: the default instance shares
        // the static process-wide client, so disposing it here would kill every
        // other feed manager's socket reuse (the latent cross-widget break).
        // The client lives for the process; only the loops are shut down.
    }
}
