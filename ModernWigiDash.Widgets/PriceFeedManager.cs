using System.Collections.Concurrent;

namespace ModernWigiDash.Widgets;

/// <summary>
/// The asset kind a ticker symbol tracks; selects the subscription routing
/// and the one-shot fallback seed's source leg.
/// </summary>
public enum AssetKind
{
    /// <summary>Cryptocurrency (Binance WebSocket + REST, CoinGecko seed).</summary>
    Crypto,
    /// <summary>Stock (Finnhub WebSocket + REST, Yahoo chart seed).</summary>
    Stock,
    /// <summary>FX pair (Frankfurter REST only; no live WebSocket feed).</summary>
    Fx
}

/// <summary>
/// One tracked price sample in the shared map: the value, its change and
/// currency, the source that wrote it, and the stamp the staleness decision
/// reads.
/// </summary>
public class PriceInfo
{
    /// <summary>The freshness window in seconds — the one spelling shared by
    /// <see cref="IsStale"/> and the CoinGecko downgrade guard.</summary>
    internal const double FreshnessSeconds = 60;

    /// <summary>The last known price.</summary>
    public decimal Price { get; set; }
    /// <summary>The last known change percentage.</summary>
    public decimal ChangePercent { get; set; }
    /// <summary>The currency symbol rendered with the price.</summary>
    public string CurrencySymbol { get; set; } = "$";
    /// <summary>The source label that last wrote this record (the freshness guard's discriminator).</summary>
    public string Source { get; set; } = "";
    /// <summary>When the sample was stamped (the staleness decision's input).</summary>
    public DateTime Timestamp { get; set; }
    /// <summary>The change percent formatted for display (signed, invariant, two decimals).</summary>
    public string FormattedChange =>
        $"{(ChangePercent >= 0 ? "+" : "")}{ChangePercent.ToString("F2", CultureInfo.InvariantCulture)}%";
    /// <summary>Whether the change is upward (the badge's color pick).</summary>
    public bool IsPositive => ChangePercent >= 0;
    /// <summary>Whether the sample is older than the freshness window (the store's clock).</summary>
    public bool IsStale => (Clock.GetUtcNow().UtcDateTime - Timestamp).TotalSeconds > FreshnessSeconds;

    /// <summary>Test seam: clock for the staleness decision.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
}

/// <summary>
/// The shared price-streaming manager: ref-counted subscription claims, the
/// two WebSocket feed loops, and the REST cycle wiring. One REST quote leg
/// per source sits behind the manager's shared HTTP seam — legs own URL
/// shape and response parse, and this class never builds a URL or parses a
/// source payload itself. The price map and its merge policy are the
/// <see cref="PriceMapStore"/> seam: every write site routes through the
/// store's live/fallback rules, so a new source is a rule choice, not a
/// re-derivation.
/// </summary>
public sealed class PriceFeedManager : IDisposable
{
    /// <summary>The one REST poll cadence, spelled once for every source —
    /// per-interval fields could drift apart, and they did the design
    /// around them.</summary>
    internal static readonly TimeSpan RestInterval = TimeSpan.FromSeconds(30);

    private readonly string _finnhubKey;
    // The price map and every write into it are the store seam: the merge
    // rules (live overwrite, the fallback downgrade guard) and the record
    // stamping live there, against this clock read live at write time (a
    // test that swaps Clock after construction is still honored).
    private readonly PriceMapStore _store;
    // Subscriber claim counts: N widgets on one symbol hold N claims, so one
    // widget's unsubscribe only releases when the last claim leaves.
    internal readonly ConcurrentDictionary<string, int> _subscribedCrypto = new();
    internal readonly ConcurrentDictionary<string, int> _subscribedStocks = new();
    internal readonly ConcurrentDictionary<string, int> _subscribedFx = new();

    private readonly Func<IWebSocketFeed> _feedFactory;
    private readonly TimeSpan _reconnectDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private CancellationTokenSource _cts = new();
    private Task? _stockRestTask;
    private Task? _cryptoRestTask;
    private Task? _fxRestTask;
    // The first-claim startup (the ??= create + Start) and the last-release
    // teardown (cancel + dispose + null) are two non-atomic field sequences
    // over the same fields; the gate makes them one serialized unit, so a
    // startup can never read a loop the teardown just nulled (an NRE) or
    // start one the teardown just disposed (a dead socket behind a live
    // claim).
    private readonly Lock _lifecycleGate = new();
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

    /// <summary>
    /// Creates a manager over the shared process-wide HttpClient with an
    /// optional Finnhub API key for the stock quote leg.
    /// </summary>
    /// <param name="finnhubApiKey">The Finnhub API key, or null when none is configured.</param>
    public PriceFeedManager(string? finnhubApiKey = null)
        : this(SharedHttpClient, finnhubApiKey)
    {
    }

    /// <summary>Internal constructor with injectable seams: HttpClient, WebSocket feed factory, reconnect delay, loop delay.</summary>
    internal PriceFeedManager(
        HttpClient httpClient,
        string? finnhubApiKey = null,
        Func<IWebSocketFeed>? feedFactory = null,
        TimeSpan? reconnectDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _feedFactory = feedFactory ?? (() => new ClientWebSocketFeed());
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

        // The store seam gets the clock as a live read (not a captured
        // provider): the Frankfurter leg's date window and the store's
        // stamps/guard all see a test's post-construction Clock swap.
        _store = new(() => Clock.GetUtcNow().UtcDateTime);

        // One leg per REST source: the URL shape, the wire parse and the
        // source label live with the source (one leg module per source), so a
        // wire-format change touches exactly one leg.
        CryptoRestLeg = BinanceUsRestLeg.Create(httpClient);
        StockRestLeg = FinnhubRestLeg.Create(httpClient, _finnhubKey);
        FxRestLeg = FrankfurterRestLeg.Create(httpClient, () => Clock.GetUtcNow());
        YahooRestLeg = YahooChartRestLeg.Create(httpClient);
        CoinGeckoLeg = new CoinGeckoRestLeg(httpClient);

        // The per-kind feed table: each row owns the kind's validation guard,
        // ref-counted map, first-claim startup (the kind's own loop/REST order,
        // incl. its gates), and WS subscribe payload — Subscribe/Unsubscribe
        // run the shared sequence over it.
        FeedKindWiring cryptoWiring = null!;
        cryptoWiring = new FeedKindWiring
        {
            IsValid = SymbolCatalog.IsValidSymbol,
            Subscriptions = _subscribedCrypto,
            OnFirstClaim = () =>
            {
                cryptoWiring.Loop ??= CreateBinanceLoop();
                cryptoWiring.Loop.Start();
                // The loop reads the LIVE membership each cycle (a
                // ConcurrentDictionary.Keys is a snapshot per call, but the
                // view is re-read per cycle): a coin subscribed after the
                // first claim is polled on the next cycle, never frozen out.
                _cryptoRestTask ??= RestPollLoop.RunAsync(
                    RestInterval, () => !_disposed, _cts.Token,
                    () => _subscribedCrypto.Keys,
                    PollCryptoAsync,
                    _delay, _failLog,
                    FallbackCoinGeckoAsync);
            },
            WsSubscribeFrame = key => PriceFeedMessages.BuildBinanceSubscribe([key]),
            // The crypto one-shot seed: the CoinGecko leg (its id resolution
            // from the single catalog table rides along as the guard).
            Seed = new(CoinGeckoRestLeg.SourceLabel, "$", (key, ct) => CoinGeckoLeg.FetchAsync(key, ct)),
        };
        _cryptoWiring = cryptoWiring;
        FeedKindWiring stockWiring = null!;
        stockWiring = new FeedKindWiring
        {
            IsValid = SymbolCatalog.IsValidSymbol,
            Subscriptions = _subscribedStocks,
            OnFirstClaim = () =>
            {
                // A missing Finnhub key disables the stock feeds entirely; the
                // claim still counts (so Unsubscribe stays balanced) and the
                // Yahoo seed in SeedFallbackAsync keeps working as the fallback.
                if (string.IsNullOrEmpty(_finnhubKey)) return;
                stockWiring.Loop ??= CreateFinnhubLoop();
                stockWiring.Loop.Start();
                _stockRestTask ??= RestPollLoop.RunAsync(
                    RestInterval, () => !_disposed, _cts.Token,
                    () => _subscribedStocks.Keys,
                    PollStockAsync,
                    _delay, _failLog);
            },
            WsSubscribeFrame = key => PriceFeedMessages.BuildFinnhubSubscribe(key),
            // The stock one-shot seed: the Yahoo chart leg (its symbol guard
            // is the only validation the seed path has).
            Seed = new(YahooRestLeg.SourceLabel, YahooRestLeg.CurrencySymbol, (key, ct) => YahooRestLeg.FetchAsync(key, ct)),
        };
        _stockWiring = stockWiring;
        _fxWiring = new FeedKindWiring
        {
            IsValid = symbol => SymbolCatalog.IsValidFxInput(symbol, out _),
            Subscriptions = _subscribedFx,
            OnFirstClaim = () =>
            {
                _fxRestTask ??= RestPollLoop.RunAsync(
                    RestInterval, () => !_disposed, _cts.Token,
                    () => _subscribedFx.Keys,
                    PollFxAsync,
                    _delay, _failLog);
            },
            // No socket: the Frankfurter REST cycle is the FX feed.
            WsSubscribeFrame = null,
            // No seed leg: the Frankfurter cycle already serves FX (a
            // one-shot seed would duplicate it, and the series response has
            // no single best quote to seed from).
            Seed = null,
        };
    }

    /// <summary>
    /// One asset kind's feed wiring — the per-kind half that
    /// <see cref="Subscribe"/>, <see cref="Unsubscribe"/>, and
    /// <see cref="SeedFallbackAsync"/> differ on: the validation guard, the
    /// ref-counted subscription map, what the first claim starts (WS loop
    /// and/or REST cycle, in the kind's own order), the WS subscribe frame
    /// builder (null when the kind has no socket), and the one-shot seed leg
    /// (null when the kind's cycle already serves it — FX, the Frankfurter
    /// cycle). The row also owns the WS loop it starts (<see cref="Loop"/>,
    /// created lazily on the first claim and disposed at shutdown), so the
    /// loop is keyed by the row, not a parallel feed-kind enum. The shared
    /// routines own their sequences (validate → claim → start → subscribe;
    /// the seed's fetch → apply), so a fourth asset kind is one table row,
    /// not a fourth copy of the steps.
    /// </summary>
    private sealed class FeedKindWiring
    {
        public required Func<string, bool> IsValid { get; init; }
        public required ConcurrentDictionary<string, int> Subscriptions { get; init; }
        public required Action OnFirstClaim { get; init; }
        public required Func<string, string>? WsSubscribeFrame { get; init; }
        public required SeedLeg? Seed { get; init; }
        // The WS loop this row starts: created lazily on the first claim and
        // disposed at shutdown. Null while no claim has started it.
        public FeedLoop? Loop { get; set; }
    }

    /// <summary>The kind's one-shot seed leg: the source label the seeded
    /// record is stored under, its currency symbol, and the fetch (the leg's
    /// own validation guard rides along — the seed path has no subscription
    /// boundary to validate at).</summary>
    private sealed record SeedLeg(string SourceLabel, string CurrencySymbol, Func<string, CancellationToken, Task<QuoteSample?>> Fetch);

    private readonly FeedKindWiring _cryptoWiring;
    private readonly FeedKindWiring _stockWiring;
    private readonly FeedKindWiring _fxWiring;

    /// <summary>
    /// The kind's feed wiring — the one table <see cref="Subscribe"/>,
    /// <see cref="Unsubscribe"/>, and <see cref="SeedFallbackAsync"/> all
    /// route through, so the kind→(validation, map, startup, subscribe,
    /// seed) mapping is spelled exactly once. Every named kind has an arm:
    /// an unnamed value (a cast of an out-of-range int, which no boundary
    /// can produce) fails loudly instead of riding a silent default, and a
    /// new kind is one arm plus one table row.
    /// </summary>
    private FeedKindWiring WiringFor(AssetKind kind)
    {
        switch (kind)
        {
            case AssetKind.Crypto: return _cryptoWiring;
            case AssetKind.Stock: return _stockWiring;
            case AssetKind.Fx: return _fxWiring;
        }
        throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
    }

    /// <summary>
    /// Validates the symbol for the asset kind, ref-counts the claim, and on
    /// the FIRST claim for a key starts the kind's feeds and pushes the
    /// incremental WS subscribe. N widgets on one symbol hold N claims — one
    /// widget's symbol change must not kill another's live feed.
    /// </summary>
    public void Subscribe(string symbol, AssetKind kind)
    {
        FeedKindWiring wiring = WiringFor(kind);
        if (!wiring.IsValid(symbol))
        {
            SymbolCatalog.LogInvalidSymbol(symbol);
            return;
        }
        // The branch keys (alias-resolved base coin, upper-cased ticker,
        // normalized FX key) are all the catalog's ToFeedKey for the kind.
        string key = SymbolCatalog.ToFeedKey(symbol, kind);
        if (wiring.Subscriptions.AddOrUpdate(key, 1, (_, count) => count + 1) == 1)
        {
            // The claim itself is atomic (the dictionary's CAS), but the
            // first-claim startup and the last-release teardown are two
            // non-atomic field sequences over the same loop fields: the
            // lifecycle gate serializes them. The in-gate dispose re-check
            // releases a claim a dispose beat to the gate (a disposed
            // manager never starts a loop), and EnsureActive re-arms a cts
            // a racing shutdown cancelled, so a loop is never created with
            // an already-dead token.
            lock (_lifecycleGate)
            {
                if (_disposed)
                {
                    ReleaseSubscription(wiring.Subscriptions, key);
                    return;
                }
                EnsureActive();
                wiring.OnFirstClaim();
                // Push an incremental subscribe so symbols added after the socket
                // connected still receive real-time ticks (kinds with a socket).
                if (wiring.WsSubscribeFrame is not null)
                {
                    _ = SendWsSubscribeAsync(wiring, key);
                }
            }
        }
    }

    /// <summary>
    /// Sends an incremental WebSocket subscription for a symbol added after the
    /// feed socket was already connected. No-op when the socket is not open.
    /// The loop and the wire frame both ride the caller's wiring row: the
    /// frame builder is the <see cref="PriceFeedMessages"/> builder for the
    /// kind, the same spelling the connect-time payload uses.
    /// </summary>
    private async Task SendWsSubscribeAsync(FeedKindWiring wiring, string symbol)
    {
        try
        {
            IWebSocketFeed? ws = wiring.Loop?.Current;
            if (ws == null || !ws.IsOpen) return;
            await ws.SendTextAsync(wiring.WsSubscribeFrame!(symbol), _cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // Incremental subscribe is best-effort; the connect-time payload
            // covers the symbols known at that point.
            FileLog.Write($"[PRICE-FEED] Incremental feed subscribe failed for {symbol}");
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
        bool fullyReleased = ReleaseSubscription(WiringFor(kind).Subscriptions, key);

        // Prices for a fully-released symbol are stale by construction; a
        // symbol with remaining subscribers keeps its cached price.
        if (fullyReleased)
        {
            _store.TryRemove(key);
        }

        // Ref-counted shutdown: when the last subscriber leaves, stop the
        // sockets and pollers so the static per-widget feed does not hold
        // process-lifetime network handles. The empty check rides the
        // lifecycle gate (the same one Subscribe's first-claim startup
        // holds): a claim landing between this release and the check is
        // visible under the gate, so the shutdown never retires a feed a
        // racing subscribe just claimed.
        lock (_lifecycleGate)
        {
            if (_subscribedCrypto.IsEmpty && _subscribedStocks.IsEmpty && _subscribedFx.IsEmpty)
            {
                ShutdownLoops();
            }
        }
    }

    /// <summary>Releases one subscriber claim; true when the LAST claim was
    /// released (the key is removed) — a symbol with remaining subscribers
    /// keeps its key and cached price. The decrement is a compare-exchange
    /// loop: a racing release re-reads the new count instead of both
    /// applying their own (2 -> 1 twice would leave a 0-claim entry that
    /// blocks the shutdown decision and the price cleanup).</summary>
    private static bool ReleaseSubscription(ConcurrentDictionary<string, int> subscriptions, string key)
    {
        while (true)
        {
            if (!subscriptions.TryGetValue(key, out int count)) return false;
            if (count <= 1) return subscriptions.TryRemove(key, out _);
            if (subscriptions.TryUpdate(key, count - 1, count)) return false;
        }
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
        CancellationTokenSource current = Volatile.Read(ref _cts);
        if (!current.IsCancellationRequested) return;

        // Compare-exchange so only the caller that actually replaced the
        // cancelled source retires it. A racing swap that loses the CAS
        // publishes nothing (its replacement is disposed immediately, before
        // any work could observe it) and adopts the winner's live source.
        // The plain exchange retired the winner's fresh source too, and its
        // grace-window dispose then killed a live token that in-flight work
        // still held.
        while (true)
        {
            var replacement = new CancellationTokenSource();
            CancellationTokenSource replaced = Interlocked.CompareExchange(ref _cts, replacement, current);
            if (replaced == current)
            {
                RetireAfterGrace(replaced);
                return;
            }
            replacement.Dispose();
            current = replaced;
            if (!current.IsCancellationRequested) return;
        }
    }

    private static void RetireAfterGrace(CancellationTokenSource retired)
    {
        // The old source's token may still be held by in-flight work the
        // shutdown cancelled (a mid-flight REST poll's HTTP cancellation
        // registration). Retire it after a grace window so those
        // registrations unwind first — disposing it now would turn the
        // expected cancellations into ObjectDisposedExceptions. Cancel()
        // aborts in-flight requests synchronously through the token
        // callbacks; the window covers the queued cancellation callbacks
        // unwinding, not request latency.
        _ = Task.Delay(RetiredCtsGrace, CancellationToken.None).ContinueWith(_ => retired.Dispose(), TaskScheduler.Default);
    }

    /// <summary>The grace window before a retired (cancelled) CTS is disposed —
    /// see <see cref="EnsureActive"/>.</summary>
    internal static readonly TimeSpan RetiredCtsGrace = TimeSpan.FromSeconds(30);

    /// <summary>Cancels the feed loops and closes the sockets when no subscribers
    /// remain. The caller holds <see cref="_lifecycleGate"/>: the teardown and
    /// the first-claim startup are one serialized unit.</summary>
    private void ShutdownLoops()
    {
        _cts.Cancel();
        DisposeLoops();
        _stockRestTask = null;
        _cryptoRestTask = null;
        _fxRestTask = null;
    }

    /// <summary>The one loop-teardown both shutdown paths route through: each
    /// wiring row disposes (and nulls) the WS loop it owns, so the loop's
    /// lifetime is keyed by the row at both its ends.</summary>
    private void DisposeLoops()
    {
        _cryptoWiring.Loop?.Dispose();
        _cryptoWiring.Loop = null;
        _stockWiring.Loop?.Dispose();
        _stockWiring.Loop = null;
    }

    /// <summary>
    /// The shared map's current sample for the symbol under the kind's feed
    /// key, or null when the symbol has no price yet.
    /// </summary>
    /// <param name="symbol">The ticker symbol or coin name.</param>
    /// <param name="kind">The asset kind (selects the feed key shape).</param>
    /// <returns>The current sample, or null when untracked or absent.</returns>
    public PriceInfo? GetPrice(string symbol, AssetKind kind)
    {
        string key = SymbolCatalog.ToFeedKey(symbol, kind);
        return _store.TryGet(key);
    }

    /// <summary>
    /// The one-shot fallback seed — the manager's single seeding operation,
    /// which the ticker's render tick and the subscription seed ride. The
    /// kind's seed leg is a column of the kind table (crypto → CoinGecko,
    /// stock → Yahoo, FX → none), and the map write rides the store's
    /// fallback rule (a fresh live price is never downgraded — the same rule
    /// the crypto-cycle batch tail applies). It never throws, so a
    /// fire-and-forget caller is safe.
    /// </summary>
    public async Task SeedFallbackAsync(string symbol, AssetKind kind)
    {
        if (WiringFor(kind).Seed is not SeedLeg seed) return;
        string key = SymbolCatalog.ToFeedKey(symbol, kind);
        try
        {
            QuoteSample? sample = await seed.Fetch(key, _cts.Token).ConfigureAwait(false);
            if (sample is not QuoteSample quote) return;
            _store.ApplyFallback(key, quote.Price, quote.ChangePercent, seed.SourceLabel, seed.CurrencySymbol);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transport failure: the leg propagates it (the REST cycle isolates
            // per-symbol; the one-shot path has no cycle) — the seed's owner is
            // the cadence-deduped log, so a dead source stays diagnosable.
            _failLog.Write($"One-shot fallback seed failed for {LogSanitizer.Sanitize(symbol)}: {ex.Message}");
        }
    }

    /// <summary>Diagnostic log with cadence dedup for the per-tick feed
    /// failures — the module's runtime surface (configuration paths use
    /// FileLog directly). Every Nth failure writes, so a dead feed is
    /// diagnosable in the field without a per-tick log storm.</summary>
    private readonly DiagLog _failLog = new("PRICE-FEED", 20, logFirst: true);

    private FeedLoop CreateBinanceLoop() => new(
        new Uri("wss://stream.binance.us:9443/ws"),
        _feedFactory,
        (feed, ct) => feed.SendTextAsync(PriceFeedMessages.BuildBinanceSubscribe(_subscribedCrypto.Keys), ct),
        ParseBinanceTicker,
        new FixedReconnectPolicy(_reconnectDelay));

    private FeedLoop CreateFinnhubLoop() => new(
        new Uri($"wss://ws.finnhub.io?token={_finnhubKey}"),
        _feedFactory,
        async (feed, ct) =>
        {
            foreach (var sym in _subscribedStocks.Keys)
                await feed.SendTextAsync(PriceFeedMessages.BuildFinnhubSubscribe(sym), ct).ConfigureAwait(false);
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
    /// request and the parse; the store owns the map write (the leg never
    /// touches the price map).</summary>
    private async Task PollLegAsync(string key, PriceRestLeg leg)
    {
        QuoteSample? sample = await leg.FetchAsync(key, _cts.Token).ConfigureAwait(false);
        if (sample is not QuoteSample quote) return;
        _store.ApplyLive(key, quote.Price, quote.ChangePercent, leg.SourceLabel, leg.CurrencySymbol);
    }

    /// <summary>
    /// The crypto cycle's batch tail: one CoinGecko request for every
    /// subscribed base coin. A fresh live price is never downgraded by the
    /// fallback (the store's fallback rule, see
    /// <see cref="PriceMapStore.ApplyFallback"/>).
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
                _store.ApplyFallback(key, sample.Price, sample.ChangePercent, CoinGeckoRestLeg.SourceLabel);
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
        _store.ApplyLive(coin, price, change, "Binance");
    }

    private void ParseFinnhubMessage(string json)
    {
        if (!PriceFeedMessages.TryParseFinnhubTrades(json, out var trades))
        {
            _failLog.Write("Failed to parse Finnhub message; ignoring");
            return;
        }
        // A trade message carries no change figure: the store's live rule
        // keeps the previously known change (the quote leg's) instead of
        // zeroing it.
        foreach (var trade in trades)
        {
            _store.ApplyLive(trade.Symbol, trade.Price, null, "Finnhub");
        }
    }

    /// <summary>
    /// Cancels the feed loops and shuts them down; the shared HttpClient is
    /// deliberately kept alive (it is process-wide, not owned here).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Under the lifecycle gate: a first-claim startup holding the gate
        // either finishes first (its loops are then retired here) or sees
        // the dispose flag and releases its claim instead of starting.
        lock (_lifecycleGate)
        {
            _cts.Cancel();
            // Deliberately NOT disposed here: fire-and-forget sends may still be
            // awaiting with this token (the loops break on OCE and never re-touch
            // it); the codebase's deferral pattern lets the source be GC'ed.
            DisposeLoops();
        }
        // The manager never owns its HttpClient: the default instance shares
        // the static process-wide client, so disposing it here would kill every
        // other feed manager's socket reuse (the latent cross-widget break).
        // The client lives for the process; only the loops are shut down.
    }
}
