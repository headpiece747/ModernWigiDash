namespace ModernWigiDash.Widgets;

/// <summary>
/// Encapsulates zero-allocation Skia rendering for the stock &amp; crypto ticker widget.
/// Hoists all 5 <see cref="SKPaint"/> instances, caches display label and price formatting strings,
/// and handles font sizing, text truncation, freshness badges, and placeholder presentation.
/// </summary>
internal sealed class CryptoStockTickerCanvas : IDisposable
{
    // Hoisted paints (the 30 FPS render allocates no SKPaint).
    private readonly SKPaint _symbolPaint = new() { IsAntialias = true };
    private readonly SKPaint _pricePaint = new() { IsAntialias = true };
    private readonly SKPaint _badgePaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderTitlePaint = new() { IsAntialias = true };
    private readonly SKPaint _placeholderHintPaint = new() { IsAntialias = true };

    // The label and formatted price are memoized per input: Render composes
    // them every frame, but the inputs change only via the inspector (label
    // keys) or the feed's ~1×/s price updates. Single-slot caches keyed by the
    // source values, so identical inputs produce identical strings with no
    // per-frame formatting work.
    private readonly MemoSlot<(string Symbol, string AssetType, string DisplayName), string> _labelMemo = new();

    /// <summary>
    /// Gets the formatted display label, memoized per (symbol, assetType, displayName) combination.
    /// </summary>
    public string GetDisplayLabel(string symbol, string assetType, string displayName, bool isFxAsset)
    {
        var key = (symbol, assetType, displayName);
        if (_labelMemo.TryGet(key, out var value))
        {
            return value;
        }

        string? fxLabel = isFxAsset && SymbolCatalog.TryParseFxPair(symbol, out string baseCur, out string quoteCur)
            ? $"{baseCur} / {quoteCur}"
            : null;
        value = TickerPresentation.DisplayLabel(displayName, fxLabel, SymbolCatalog.NormalizeSymbol(symbol));
        _labelMemo.Set(key, value);
        return value;
    }

    private readonly MemoSlot<(decimal Price, string Decimals, string Currency), string> _priceMemo = new();

    /// <summary>
    /// Formats the raw numeric price according to the specified decimal configuration and currency symbol.
    /// </summary>
    public string FormatPrice(decimal rawPrice, string priceDecimals, string currencySymbol = "$")
    {
        var key = (rawPrice, priceDecimals, currencySymbol);
        if (_priceMemo.TryGet(key, out var value))
        {
            return value;
        }

        value = TickerPresentation.FormatPrice(rawPrice, priceDecimals, currencySymbol);
        _priceMemo.Set(key, value);
        return value;
    }

    /// <summary>
    /// Renders the active symbol ticker: symbol label (truncated to width), price, and change badge.
    /// </summary>
    public void RenderTicker(
        SKCanvas canvas,
        SKRect bounds,
        string displayLabel,
        string price,
        string changeBadge,
        bool isPositive,
        bool isStale,
        bool showChange,
        SKColor textColor,
        SKColor posColor,
        SKColor negColor)
    {
        float pad = 14f;
        float priceSize = Math.Min(bounds.Width / 6f, bounds.Height / 3.5f);

        var symFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, priceSize);
        _symbolPaint.Color = textColor;
        string symbolText = TextRenderHelper.TruncateText(displayLabel, symFont, _symbolPaint, bounds.Width - pad * 2f);
        canvas.DrawTextWithFallback(symbolText, pad, pad + priceSize * 0.8f, symFont, _symbolPaint);

        var priceFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, priceSize);
        _pricePaint.Color = textColor;
        canvas.DrawTextWithFallback(price, pad, bounds.MidY + priceSize * 0.35f, priceFont, _pricePaint);

        if (showChange)
        {
            // Stale prices render in a neutral gray with a freshness dot so the
            // last-known value is never mistaken for live data.
            SKColor badgeColor = isPositive ? posColor : negColor;
            if (isStale) badgeColor = textColor.WithAlpha(TickerStalenessPresentation.StaleBadgeAlpha);
            var badgeFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, priceSize);
            _badgePaint.Color = badgeColor;
            string badgeText = TickerStalenessPresentation.BadgeText(changeBadge, isStale);
            canvas.DrawTextWithFallback(badgeText, pad, bounds.Bottom - pad, badgeFont, _badgePaint);
        }
    }

    /// <summary>
    /// Draws the empty-state placeholder prompting the user to enter a symbol.
    /// </summary>
    public void RenderPlaceholder(SKCanvas canvas, SKRect bounds, SKColor textColor)
    {
        float mainSize = Math.Min(bounds.Width / 6f, bounds.Height / 3.5f);

        var titleFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Bold, Math.Max(mainSize * 0.55f, 13f));
        _placeholderTitlePaint.Color = textColor;
        TextRenderHelper.DrawCenteredText(canvas, "Enter a symbol", bounds.MidX, bounds.MidY - 4f, titleFont, _placeholderTitlePaint);

        var hintFont = FontHelper.GetCachedFont("Geist", SKFontStyle.Normal, Math.Max(mainSize * 0.4f, 11f));
        _placeholderHintPaint.Color = textColor.WithAlpha(160);
        TextRenderHelper.DrawCenteredText(canvas, "e.g. BTC, ETH, AAPL, MSFT", bounds.MidX, bounds.MidY + 16f, hintFont, _placeholderHintPaint);
    }

    /// <summary>Disposes all hoisted Skia paints.</summary>
    public void Dispose()
    {
        _symbolPaint.Dispose();
        _pricePaint.Dispose();
        _badgePaint.Dispose();
        _placeholderTitlePaint.Dispose();
        _placeholderHintPaint.Dispose();
    }
}
