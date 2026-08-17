using ModernWigiDash.Sdk;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("ticker_stock", "Stock & Crypto", Category = "Utilities", DefaultGridSize = GridSizePreset.Size1x1)]
public class CryptoStockTickerWidget : ModernWidgetBase
{
    [WidgetProperty("Symbol", WidgetPropertyType.Text, "Crypto name (bitcoin, solana) or stock ticker (AAPL, MSFT)")]
    public string Symbol { get; set; } = "";

    [WidgetProperty("Asset Type", WidgetPropertyType.Choice, "Force type when auto-detection doesn't recognize your symbol", "Auto", "Auto", "Crypto", "Stock", "FX Pair")]
    public string AssetType { get; set; } = "Auto";

    [WidgetProperty("Display Name", WidgetPropertyType.Text, "Optional custom label (leave blank to auto-generate from symbol)")]
    public string DisplayName { get; set; } = "";

    private string Price = "";

    private string ChangeBadge = "";

    [WidgetProperty("Show Change", WidgetPropertyType.Boolean, "Show or hide the change percentage badge", true)]
    public bool ShowChange { get; set; } = true;

    [WidgetProperty("Price Decimals", WidgetPropertyType.Choice, "Decimal places for small-value assets (Auto adjusts to price)", "Auto", "Auto", "2", "4", "6", "8")]
    public string PriceDecimals { get; set; } = "Auto";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Symbol and price color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Positive Color", WidgetPropertyType.Color, "Upward change badge color", "#22C55E")]
    public string PositiveColorHex { get; set; } = "#22C55E";

    [WidgetProperty("Negative Color", WidgetPropertyType.Color, "Downward change badge color", "#EF4444")]
    public string NegativeColorHex { get; set; } = "#EF4444";

    /// <summary>One process-wide manager shared by every ticker widget (the
    /// default). Tests inject a manager with a fake feed/HttpClient.</summary>
    internal PriceFeedManager Feed { get; set; } = SharedFeed;

    private static readonly PriceFeedManager SharedFeed = new();
    private readonly FeedSubscription _subscription;
    private readonly TickerFallbackPolicy _fallbackPolicy;
    private bool _lastChangePositive;

    public CryptoStockTickerWidget()
    {
        _subscription = new(() => _ = FallbackFetchAsync());
        // The policy reads the Clock seam lazily, so tests that swap Clock
        // after construction still drive the cadence.
        _fallbackPolicy = new(() => Clock);
    }

    /// <summary>Test seam for the fallback-fetch throttle.</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    public override async ValueTask InitializeAsync(IModernWigiDashContext context, CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(context, cancellationToken).ConfigureAwait(false);
        UpdateSubscription();
    }

    /// <summary>
    /// Re-subscribes when the feed identity (Symbol / AssetType) changes —
    /// inspector writes funnel through OnPropertyChanged, so Render stays a
    /// pure draw (the old code subscribed as a draw side effect at 30 FPS).
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

    public override ValueTask DisposeAsync()
    {
        // Stop polling this widget's symbol once it is removed from the canvas.
        _subscription.Untrack();
        return base.DisposeAsync();
    }

    private AssetKind AssetKindValue => SymbolCatalog.DetectAssetKind(Symbol, AssetType);
    private bool IsFxAsset => AssetKindValue == AssetKind.Fx;

    // The label and formatted price are memoized per input: Render composes
    // them every frame, but the inputs change only via the inspector (label
    // keys) or the feed's ~1×/s price updates. Single-slot caches keyed by the
    // source values, so identical inputs produce identical strings with no
    // per-frame formatting work.
    private string _labelKeySymbol = "";
    private string _labelKeyType = "";
    private string _labelKeyName = "";
    private string _labelText = "";

    private string DisplayLabel
    {
        get
        {
            if (!string.Equals(Symbol, _labelKeySymbol, StringComparison.Ordinal) || !string.Equals(AssetType, _labelKeyType, StringComparison.Ordinal) || !string.Equals(DisplayName, _labelKeyName, StringComparison.Ordinal))
            {
                _labelKeySymbol = Symbol;
                _labelKeyType = AssetType;
                _labelKeyName = DisplayName;
                string? fxLabel = IsFxAsset && SymbolCatalog.TryParseFxPair(Symbol, out string baseCur, out string quoteCur)
                    ? $"{baseCur} / {quoteCur}"
                    : null;
                _labelText = TickerPresentation.DisplayLabel(DisplayName, fxLabel, SymbolCatalog.NormalizeSymbol(Symbol));
            }
            return _labelText;
        }
    }

    private readonly MemoSlot<(decimal Price, string Decimals, string Currency), string> _priceMemo = new();

    private string FormatPrice(decimal rawPrice, string currencySymbol = "$")
        => _priceMemo.GetOrCompute(
            (rawPrice, PriceDecimals, currencySymbol),
            () => TickerPresentation.FormatPrice(rawPrice, PriceDecimals, currencySymbol));

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        if (string.IsNullOrWhiteSpace(Symbol))
        {
            DrawPlaceholder(canvas, bounds);
            return;
        }

        AssetKind kind = AssetKindValue;
        var info = Feed.GetPrice(Symbol, kind);
        bool isStale = TickerStalenessPresentation.IsStale(info);
        if (info != null)
        {
            Price = FormatPrice(info.Price, info.CurrencySymbol);
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
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        SKColor posColor = ColorOf(PositiveColorHex, new SKColor(34, 197, 94));
        SKColor negColor = ColorOf(NegativeColorHex, new SKColor(239, 68, 68));

        float pad = 14f;
        float priceSize = Math.Min(bounds.Width / 6f, bounds.Height / 3.5f);

        var symFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, priceSize);
        using var symPaint = new SKPaint { Color = textColor, IsAntialias = true };
        string symbolText = TextRenderHelper.TruncateText(DisplayLabel, symFont, symPaint, bounds.Width - pad * 2f);
        canvas.DrawTextWithFallback(symbolText, pad, pad + priceSize * 0.8f, symFont, symPaint);

        var priceFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, priceSize);
        using var pricePaint = new SKPaint { Color = textColor, IsAntialias = true };
        canvas.DrawTextWithFallback(Price, pad, bounds.MidY + priceSize * 0.35f, priceFont, pricePaint);

        if (ShowChange)
        {
            // Stale prices render in a neutral gray with a freshness dot so the
            // last-known value is never mistaken for live data.
            SKColor badgeColor = isPositive ? posColor : negColor;
            if (isStale) badgeColor = textColor.WithAlpha(TickerStalenessPresentation.StaleBadgeAlpha);
            var badgeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, priceSize);
            using var badgePaint = new SKPaint
            {
                Color = badgeColor,
                IsAntialias = true
            };
            string badgeText = TickerStalenessPresentation.BadgeText(ChangeBadge, isStale);
            canvas.DrawTextWithFallback(badgeText, pad, bounds.Bottom - pad, badgeFont, badgePaint);
        }
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds)
    {
        SKColor textColor = ColorOf(TextColorHex, SKColors.White);
        float mainSize = Math.Min(bounds.Width / 6f, bounds.Height / 3.5f);

        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(mainSize * 0.55f, 13f));
        using var titlePaint = new SKPaint { Color = textColor, IsAntialias = true };
        TextRenderHelper.DrawCenteredText(canvas, "Enter a symbol", bounds.MidX, bounds.MidY - 4f, titleFont, titlePaint);

        var hintFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(mainSize * 0.4f, 11f));
        using var hintPaint = new SKPaint { Color = textColor.WithAlpha(160), IsAntialias = true };
        TextRenderHelper.DrawCenteredText(canvas, "e.g. BTC, ETH, AAPL, MSFT", bounds.MidX, bounds.MidY + 16f, hintFont, hintPaint);
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
