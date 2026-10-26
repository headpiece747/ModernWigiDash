namespace ModernWigiDash.Widgets;

/// <summary>
/// The Stock &amp; Crypto ticker widget: tracks one symbol through the shared
/// PriceFeedManager (crypto, stock, or FX), delegates typography and badge drawing
/// to <see cref="CryptoStockTickerCanvas"/>, and seeds the one-shot fallback when the live feed has no
/// price yet.
/// </summary>
[WidgetMetadata("ticker_stock", "Stock & Crypto", Category = "Utilities", DefaultGridSize = GridSizePreset.Size1x1)]
public class CryptoStockTickerWidget : ModernWidgetBase
{
    /// <summary>The "Symbol": the crypto name (bitcoin, solana) or stock ticker (AAPL, MSFT) to track.</summary>
    [WidgetProperty("Symbol", WidgetPropertyType.Text, "Crypto name (bitcoin, solana) or stock ticker (AAPL, MSFT)")]
    public string Symbol { get; set; } = "";

    /// <summary>The "Asset Type": forces the asset kind when auto-detection does not recognize the symbol (Auto, Crypto, Stock, or FX Pair).</summary>
    [WidgetProperty("Asset Type", WidgetPropertyType.Choice, "Force type when auto-detection doesn't recognize your symbol", "Auto", "Auto", "Crypto", "Stock", "FX Pair")]
    public string AssetType { get; set; } = "Auto";

    /// <summary>The "Display Name": an optional custom label (blank = auto-generated from the symbol).</summary>
    [WidgetProperty("Display Name", WidgetPropertyType.Text, "Optional custom label (leave blank to auto-generate from symbol)")]
    public string DisplayName { get; set; } = "";

    private string Price = "";
    private string ChangeBadge = "";

    /// <summary>The "Show Change" toggle: show or hide the change percentage badge.</summary>
    [WidgetProperty("Show Change", WidgetPropertyType.Boolean, "Show or hide the change percentage badge", true)]
    public bool ShowChange { get; set; } = true;

    /// <summary>The "Price Decimals": decimal places for small-value assets (Auto adjusts to the price).</summary>
    [WidgetProperty("Price Decimals", WidgetPropertyType.Choice, "Decimal places for small-value assets (Auto adjusts to price)", "Auto", "Auto", "2", "4", "6", "8")]
    public string PriceDecimals { get; set; } = "Auto";

    /// <summary>The "Text Color": the symbol and price color.</summary>
    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Symbol and price color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    /// <summary>The "Positive Color": the upward change badge color.</summary>
    [WidgetProperty("Positive Color", WidgetPropertyType.Color, "Upward change badge color", "#22C55E")]
    public string PositiveColorHex { get; set; } = "#22C55E";

    /// <summary>The "Negative Color": the downward change badge color.</summary>
    [WidgetProperty("Negative Color", WidgetPropertyType.Color, "Downward change badge color", "#EF4444")]
    public string NegativeColorHex { get; set; } = "#EF4444";

    /// <summary>One process-wide manager shared by every ticker widget (the
    /// default). Tests inject a manager with a fake feed/HttpClient.</summary>
    internal PriceFeedManager Feed { get; set; } = SharedFeed;

    private static readonly PriceFeedManager SharedFeed = new();
    private readonly FeedSubscription _subscription;
    private bool _disposed;
    private readonly TickerFallbackPolicy _fallbackPolicy;
    private bool _lastChangePositive;
    private readonly CryptoStockTickerCanvas _canvas = new();

    /// <summary>Builds the feed-identity subscription tracker and the fallback-seed cadence policy bound to the Clock seam.</summary>
    public CryptoStockTickerWidget()
    {
        _subscription = new(() => _ = FallbackFetchAsync());
        // The policy reads the Clock seam lazily, so tests that swap Clock
        // after construction still drive the cadence.
        _fallbackPolicy = new(() => Clock);
    }

    /// <summary>Test seam for the fallback-fetch throttle.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Hands the context to the base and (re)subscribes the feed identity so
    /// the shared feed tracks this widget's symbol.
    /// </summary>
    /// <param name="context">The widget context handed to the widget on load.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        UpdateSubscription();
    }

    /// <summary>
    /// Re-subscribes when the feed identity (Symbol / AssetType) changes —
    /// inspector writes funnel through OnPropertyChanged, so Render stays a
    /// pure draw.
    /// </summary>
    public override void OnPropertyChanged(string propertyName, object? newValue)
    {
        base.OnPropertyChanged(propertyName, newValue);
        if (propertyName is nameof(Symbol) or nameof(AssetType))
        {
            UpdateSubscription();
        }
    }

    private void UpdateSubscription()
    {
        _subscription.Track(Symbol, AssetKindValue, Feed);
    }

    /// <summary>Unsubscribes the symbol from the shared feed and disposes the hoisted paints.</summary>
    public override ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        // Stop polling this widget's symbol once it is removed from the canvas.
        _subscription.Untrack();
        _canvas.Dispose();
        return base.DisposeAsync();
    }

    private AssetKind AssetKindValue => SymbolCatalog.DetectAssetKind(Symbol, AssetType);
    private bool IsFxAsset => AssetKindValue == AssetKind.Fx;

    private string DisplayLabel => _canvas.GetDisplayLabel(Symbol, AssetType, DisplayName, IsFxAsset);

    /// <summary>
    /// Draws the symbol label, the price, and the change badge (stale prices
    /// render neutral with a freshness dot), or the placeholder when no
    /// symbol is set; seeds the one-shot fallback when the live feed has no
    /// price yet.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="bounds">The widget's bounds in canvas coordinates.</param>
    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);

        if (string.IsNullOrWhiteSpace(Symbol))
        {
            _canvas.RenderPlaceholder(canvas, bounds, textColor);
            return;
        }

        AssetKind kind = AssetKindValue;
        var info = Feed.GetPrice(Symbol, kind);
        // The read site's clock, named here because the record's stamp came from
        // the store's injected clock: deciding against an ambient clock instead
        // would compare a stamped time against a different clock's now.
        bool isStale = TickerStalenessPresentation.IsStale(info, Clock.GetUtcNow().UtcDateTime);
        if (info != null)
        {
            Price = _canvas.FormatPrice(info.Price, PriceDecimals, info.CurrencySymbol);
            ChangeBadge = info.FormattedChange;
            _lastChangePositive = info.IsPositive;
        }
        else if (_fallbackPolicy.TryBeginFallback())
        {
            // Read-side freshness policy: seed/re-seed the price when the live
            // feed has nothing yet, at most once per 15s (the cadence lives in
            // the policy module, not the pixel path).
            _ = FallbackFetchAsync();
        }

        bool isPositive = info?.IsPositive ?? _lastChangePositive;
        SKColor posColor = ColorOf(PositiveColorHex, new SKColor(34, 197, 94));
        SKColor negColor = ColorOf(NegativeColorHex, new SKColor(239, 68, 68));

        _canvas.RenderTicker(canvas, bounds, DisplayLabel, Price, ChangeBadge, isPositive, isStale, ShowChange, textColor, posColor, negColor);
    }

    /// <summary>
    /// One fallback seed through the manager's seam: the source routing, the FX
    /// no-op, the guarded price-map write, and the failure log all live in the
    /// manager (it owns the one-shot legs and never throws — this call is
    /// fire-and-forget from the render tick). Render pulls the seeded price from
    /// the shared map on the next tick; no write-back is needed.
    /// </summary>
    private Task FallbackFetchAsync() => Feed.SeedFallbackAsync(Symbol, AssetKindValue);
}
