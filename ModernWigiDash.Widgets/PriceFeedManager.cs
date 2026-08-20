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
    /// <summary>The freshness window in seconds — the one spelling shared by
    /// <see cref="IsStale"/> and the CoinGecko downgrade guard.</summary>
    internal const double FreshnessSeconds = 60;

    public decimal Price { get; set; }
    public decimal ChangePercent { get; set; }
    public string CurrencySymbol { get; set; } = "$";
    public string Source { get; set; } = "";
    public DateTime Timestamp { get; set; }
    public string FormattedChange =>
        $"{(ChangePercent >= 0 ? "+" : "")}{ChangePercent.ToString("F2", CultureInfo.InvariantCulture)}%";
    public bool IsPositive => ChangePercent >= 0;
    public bool IsStale => (Clock.GetUtcNow().UtcDateTime - Timestamp).TotalSeconds > FreshnessSeconds;

    /// <summary>Test seam: clock for the staleness decision.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
}

/// <summary>
/// The shared price-streaming manager: ref-counted subscription claims, the
/// price-map policy (freshness stamp, the BinanceUS downgrade guard), the
/// two WebSocket feed loops, and the REST cycle wiring. One REST quote leg
/// per source sits behind the manager's shared HTTP seam — legs own URL
/// shape and response parse, and this class never builds a URL or parses a
/// source payload itself.
/// </summary>
public sealed class PriceFeedManager : IDisposable
{
    internal enum FeedKind
    {
        Binance,
        Finnhub
    }

    /// <summary>The one REST poll cadence, spelled once for every source —
    /// per-interval fields could drift apart, and they did the design
    /// around them.</summary>
    internal static readonly TimeSpan RestInterval = TimeSpan.FromSeconds(30);

    private readonly string _finnhubKey;
    private readonly ConcurrentDictionary<string, PriceInfo> _prices = new();
    // Subscriber claim counts: N widgets on one symbol hold N claims, so one
    // widget's unsubscribe only releases when the last claim leaves.
    internal readonly ConcurrentDictionary<string, int> _subscribedCrypto = new();
    internal readonly ConcurrentDictionary<string, int> _subscribedStocks = new();
    internal readonly ConcurrentDictionary<string, int> _subscribedFx = new();

    private readonly Func<FeedKind, IWebSocketFeed> _feedFactory;
    private readonly TimeSpan _reconnectDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource _cts = new();
    private FeedLoop? _binanceLoop;
    private FeedLoop? _finnhubLoop;
    private Task? _stockRestTask;
    private Task? _cryptoRestTask;
    private Task? _fxRestTask;
    private bool _disposed;

    /// <summary>Test seam: injectable clock for price timestamps and staleness.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>The BinanceUS 24h-ticker leg (the crypto REST cycle).</summary>
    internal PriceRestLeg CryptoRestLeg { get; }

    /// <summary>The Finnhub quote leg (the stock REST cycle; the API key
    /// rides the URL).</summary>
    internal PriceRestLeg StockRestLeg { get; }

    /// <summary>The Frankfurter series leg (the FX REST cycle; the date
    /// window is built from the live <see cref="Clock"/> at poll time).</summary>
    internal PriceRestLeg FxRestLeg { get; }

    /// <summary>The Yahoo chart leg — the stock one-shot fallback only
    /// (stocks ride Finnhub on the REST cycle).</summary>
    internal PriceRestLeg YahooRestLeg { get; }

    /// <summary>The CoinGecko leg — the crypto cycle's batch fallback and
    /// the crypto one-shot fetch share its URL shape.</summary>
    internal CoinGeckoRestLeg CoinGeckoLeg { get; }

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

    /// <summary>Internal constructor with injectable seams: HttpClient, WebSocket feed factory, reconnect delay, loop delay.</summary>
    internal PriceFeedManager(
        HttpClient httpClient,
        string? finnhubApiKey = null,
        Func<FeedKind, IWebSocketFeed>? feedFactory = null,
        TimeSpan? reconnectDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _feedFactory = feedFactory ?? (_ => new ClientWebSocketFeed());
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(5);
        // The loop-delay seam (the FeedLoop pattern): tests drive the REST
        // cycle's cadence with a fake clock; production uses real delays.
        _delay = delay ?? Task.Delay;
        // The client is handed to every REST leg at construction and never
        // read again here: the legs are the module's HTTP adapters, the
        // manager itself makes no requests.
        // The Finnhub key must come from an explicit argument or the
        // FINNHUB_API_KEY environment variable — never from source control.
        _finnhubKey = finnhubApiKey ?? Environment.GetEnvironmentVariable("FINNHUB_API_KEY") ?? "";
        if (string.IsNullOrEmpty(_finnhubKey))
        {
            FileLog.Write("[PRICE-FEED] FINNHUB_API_KEY not configured — stock WebSocket/REST feeds disabled. Set the FINNHUB_API_KEY environment variable or pass the key to the constructor. Yahoo Finance fallback still works.");
        }
        // Idempotent across instances that share a client (the static default).
        httpClient.DefaultRequestHeaders.UserAgent.TryParseAdd("ModernWigiDash/2.0");

        // One leg per REST source: the URL shape, the wire parse and the
        // source label live with the source (one leg module per source), so a
        // wire-format change touches exactly one leg.
        CryptoRestLeg = BinanceUsRestLeg.Create(httpClient);
        StockRestLeg = FinnhubRestLeg.Create(httpClient, _finnhubKey);
        FxRestLeg = FrankfurterRestLeg.Create(httpClient, () => Clock.GetUtcNow());
        YahooRestLeg = YahooChartRestLeg.Create(httpClient);
        CoinGeckoLeg = new CoinGeckoRestLeg(httpClient);
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
                    _cryptoRestTask ??= RestPollLoop.RunAsync(
                        RestInterval, () => !_disposed, _cts.Token,
                        _subscribedCrypto.Keys,
                        PollCryptoAsync,
                        _delay, _failLog,
                        FallbackCoinGeckoAsync);
                    // Push an incremental subscribe so symbols added after the
                    // socket connected still receive real-time ticks.
                    _ = SendWsSubscribeAsync(FeedKind.Binance, $"{baseCoin.ToLowerInvariant()}usdt@ticker");
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
                    _fxRestTask ??= RestPollLoop.RunAsync(
                        RestInterval, () => !_disposed, _cts.Token,
                        _subscribedFx.Keys,
                        PollFxAsync,
                        _delay, _failLog);
                }
                break;
            default:
                if (!SymbolCatalog.IsValidSymbol(symbol))
                {
                    SymbolCatalog.LogInvalidSymbol(symbol);
                    return;
                }
                string stockSym = symbol.ToUpperInvariant();
                // Without a Finnhub key the WS/REST stock feeds cannot work; the
                // Yahoo Finance seed in SeedFallbackAsync still does.
                if (_subscribedStocks.AddOrUpdate(stockSym, 1, (_, count) => count + 1) == 1
                    && !string.IsNullOrEmpty(_finnhubKey))
                {
                    _finnhubLoop ??= CreateFinnhubLoop();
                    _finnhubLoop.Start();
                    _stockRestTask ??= RestPollLoop.RunAsync(
                        RestInterval, () => !_disposed, _cts.Token,
                        _subscribedStocks.Keys,
                        PollStockAsync,
                        _delay, _failLog);
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
            await ws.SendTextAsync(JsonSerializer.Serialize(message), _cts.Token).ConfigureAwait(false);
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
    /// The one-shot fallback seed — the manager's single seeding operation, which
    /// the ticker's render tick and the subscription seed ride. It owns the source
    /// routing (crypto → CoinGecko leg, stock → Yahoo leg, FX is a no-op — the
    /// Frankfurter cycle already serves FX), the price-map write under the fallback
    /// downgrade guard (a fresh live price is never downgraded — the same rule the
    /// crypto-cycle batch tail applies), and the failure log with cadence dedup. It
    /// never throws, so a fire-and-forget caller is safe.
    /// </summary>
    public async Task SeedFallbackAsync(string symbol, AssetKind kind)
    {
        // FX is a no-op: the Frankfurter REST cycle already serves FX symbols —
        // a one-shot seed would duplicate it, and its series response has no
        // single best quote to seed from.
        if (kind == AssetKind.Fx) return;

        (string key, string source) = kind == AssetKind.Crypto
            ? (SymbolCatalog.ToFeedKey(symbol, AssetKind.Crypto), CoinGeckoRestLeg.SourceLabel)
            : (symbol.ToUpperInvariant(), YahooRestLeg.SourceLabel);
        try
        {
            QuoteSample? sample = kind == AssetKind.Crypto
                ? await CoinGeckoLeg.FetchAsync(key, _cts.Token).ConfigureAwait(false)
                : await YahooRestLeg.FetchAsync(key, _cts.Token).ConfigureAwait(false);
            if (sample is not QuoteSample quote) return;
            _prices.AddOrUpdate(
                key,
                _ => NewPrice(quote.Price, quote.ChangePercent, source),
                (_, existing) => ShouldKeepExisting(existing, source, Clock.GetUtcNow().UtcDateTime)
                    ? existing
                    : NewPrice(quote.Price, quote.ChangePercent, source));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transport failure: the leg propagates it (the REST cycle isolates
            // per-symbol; the one-shot path has no cycle) — the seed's owner is
            // the cadence-deduped log, so a dead source stays diagnosable.
            _failLog.Write($"One-shot fallback seed failed for {LogSanitizer.Sanitize(symbol)}: {ex.Message}");
        }
    }

    /// <summary>Builds a fresh price record for the shared map: the timestamp
    /// is always "now" and the source labels the record — one spelling for
    /// the store sites (the per-feed currency symbol stays parameterized,
    /// e.g. Frankfurter's empty symbol for cross rates).</summary>
    private PriceInfo NewPrice(decimal price, decimal? changePercent, string source, string currencySymbol = "$")
        => new()
        {
            Price = price,
            ChangePercent = changePercent ?? 0m,
            Source = source,
            Timestamp = Clock.GetUtcNow().UtcDateTime,
            CurrencySymbol = currencySymbol
        };

    /// <summary>The fallback downgrade guard — the one spelling every fallback
    /// store site (the one-shot seed, the crypto-cycle batch tail) applies
    /// before writing a fallback sample: a fresh record from any OTHER source
    /// is kept — live feed data is never downgraded by the fallback's slower
    /// cadence and coarser data. A same-source refresh and a stale record are
    /// replaced. Pure over the existing record, the incoming source, and the
    /// clock so the rule is directly testable without the price map.</summary>
    internal static bool ShouldKeepExisting(PriceInfo existing, string incomingSource, DateTime now)
        => !string.Equals(existing.Source, incomingSource, StringComparison.Ordinal)
            && (now - existing.Timestamp).TotalSeconds < PriceInfo.FreshnessSeconds;

    /// <summary>Diagnostic log with cadence dedup for the per-tick feed
    /// failures — the module's runtime surface (configuration paths use
    /// FileLog directly). Every Nth failure writes, so a dead feed is
    /// diagnosable in the field without a per-tick log storm.</summary>
    private readonly DiagLog _failLog = new("PRICE-FEED", 20, logFirst: true);

    private FeedLoop CreateBinanceLoop() => new(
        new Uri("wss://stream.binance.us:9443/ws"),
        () => _feedFactory(FeedKind.Binance),
        (feed, ct) => feed.SendTextAsync(JsonSerializer.Serialize(new { method = "SUBSCRIBE", @params = _subscribedCrypto.Keys.Select(c => $"{c.ToLowerInvariant()}usdt@ticker").ToArray(), id = 1 }), ct),
        ParseBinanceTicker,
        new FixedReconnectPolicy(_reconnectDelay));

    private FeedLoop CreateFinnhubLoop() => new(
        new Uri($"wss://ws.finnhub.io?token={_finnhubKey}"),
        () => _feedFactory(FeedKind.Finnhub),
        async (feed, ct) =>
        {
            foreach (var sym in _subscribedStocks.Keys)
                await feed.SendTextAsync(JsonSerializer.Serialize(new { type = "subscribe", symbol = sym }), ct).ConfigureAwait(false);
        },
        ParseFinnhubMessage,
        new FixedReconnectPolicy(_reconnectDelay));

    /// <summary>One crypto-cycle hop: the leg's fetch → parse, then the
    /// price-map store.</summary>
    internal Task PollCryptoAsync(string key) => PollLegAsync(key, CryptoRestLeg);

    /// <summary>One stock-cycle hop (the Finnhub quote leg).</summary>
    internal Task PollStockAsync(string key) => PollLegAsync(key, StockRestLeg);

    /// <summary>One FX-cycle hop (the Frankfurter series leg).</summary>
    internal Task PollFxAsync(string key) => PollLegAsync(key, FxRestLeg);

    /// <summary>The hop the REST cycle runs per symbol: the leg owns the
    /// request and the parse; the manager owns the map write (the leg never
    /// touches the price map).</summary>
    private async Task PollLegAsync(string key, PriceRestLeg leg)
    {
        QuoteSample? sample = await leg.FetchAsync(key, _cts.Token).ConfigureAwait(false);
        if (sample is not QuoteSample quote) return;
        _prices[key] = NewPrice(quote.Price, quote.ChangePercent, leg.SourceLabel, leg.CurrencySymbol);
    }

    /// <summary>
    /// The crypto cycle's batch tail: one CoinGecko request for every
    /// subscribed base coin. A fresh live price is never downgraded by the
    /// fallback (see <see cref="ShouldKeepExisting"/>).
    /// </summary>
    internal async Task FallbackCoinGeckoAsync()
    {
        try
        {
            IReadOnlyDictionary<string, QuoteSample>? samples =
                await CoinGeckoLeg.FetchBatchAsync(_subscribedCrypto.Keys, _cts.Token).ConfigureAwait(false);
            if (samples is null) return;
            foreach (var (key, sample) in samples)
            {
                _prices.AddOrUpdate(key,
                    _ => NewPrice(sample.Price, sample.ChangePercent, CoinGeckoRestLeg.SourceLabel),
                    (_, existing) =>
                    {
                        if (ShouldKeepExisting(existing, CoinGeckoRestLeg.SourceLabel, Clock.GetUtcNow().UtcDateTime))
                        {
                            return existing;
                        }
                        return NewPrice(sample.Price, sample.ChangePercent ?? existing.ChangePercent, CoinGeckoRestLeg.SourceLabel);
                    });
            }
        }
        catch
        {
            _failLog.Write("CoinGecko fallback price fetch failed; continuing");
        }
    }

    private void ParseBinanceTicker(string json)
    {
        if (!PriceFeedMessages.TryParseBinanceTicker(json, out var coin, out var price, out var change))
        {
            _failLog.Write("Failed to parse Binance ticker message; ignoring");
            return;
        }
        _prices[coin] = NewPrice(price, change, "Binance");
    }

    private void ParseFinnhubMessage(string json)
    {
        if (!PriceFeedMessages.TryParseFinnhubTrades(json, out var trades))
        {
            _failLog.Write("Failed to parse Finnhub message; ignoring");
            return;
        }
        foreach (var trade in trades)
        {
            _prices.AddOrUpdate(trade.Symbol, _ => NewPrice(trade.Price, null, "Finnhub"),
                (_, existing) => NewPrice(trade.Price, existing.ChangePercent, "Finnhub"));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        // Deliberately NOT disposed here: fire-and-forget sends may still be
        // awaiting with this token (the loops break on OCE and never re-touch
        // it); the codebase's deferral pattern lets the source be GC'ed.
        _binanceLoop?.Dispose();
        _finnhubLoop?.Dispose();
        // The manager never owns its HttpClient: the default instance shares
        // the static process-wide client, so disposing it here would kill every
        // other feed manager's socket reuse (the latent cross-widget break).
        // The client lives for the process; only the loops are shut down.
    }
}
