using System.Text.Json;
using ModernWigiDash.Sdk;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("ticker_stock", "Stock & Crypto", Description = "Shows live stock/crypto symbol, real-time price, and trend badges via WebSocket.", Author = "ModernWigiDash", Version = "2.0.0", Category = "Utilities", DefaultGridSize = GridSizePreset.Size1x1)]
public class CryptoStockTickerWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size1x1.ToSize();

    [WidgetProperty("Symbol", WidgetPropertyType.Text, "Crypto name (bitcoin, solana) or stock ticker (AAPL, MSFT)")]
    public string Symbol { get; set; } = "";

    [WidgetProperty("Asset Type", WidgetPropertyType.Choice, "Force type when auto-detection doesn't recognize your symbol", "Auto", "Auto", "Crypto", "Stock", "FX Pair")]
    public string AssetType { get; set; } = "Auto";

    [WidgetProperty("Display Name", WidgetPropertyType.Text, "Optional custom label (leave blank to auto-generate from symbol)")]
    public string DisplayName { get; set; } = "";

    public string Price { get; set; } = "";

    public string ChangeBadge { get; set; } = "";

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

    private static readonly PriceFeedManager _feed = new();
    private string? _lastSubscribedSymbol;
    private AssetKind _lastSubscribedKind = AssetKind.Stock;
    private DateTime _lastFallback = DateTime.MinValue;

    public override async ValueTask DisposeAsync()
    {
        // Stop polling this widget's symbol once it is removed from the canvas.
        if (_lastSubscribedSymbol != null)
        {
            _feed.Unsubscribe(_lastSubscribedSymbol, _lastSubscribedKind);
            _lastSubscribedSymbol = null;
        }
        await base.DisposeAsync();
    }

    private AssetKind AssetKindValue => PriceFeedManager.DetectAssetKind(Symbol, AssetType);
    private bool IsCryptoAsset => AssetKindValue == AssetKind.Crypto;
    private bool IsFxAsset => AssetKindValue == AssetKind.Fx;

    private string DisplayLabel
    {
        get
        {
            if (!string.IsNullOrEmpty(DisplayName)) return DisplayName;
            if (IsFxAsset && PriceFeedManager.TryParseFxPair(Symbol, out string baseCur, out string quoteCur))
                return $"{baseCur} / {quoteCur}";
            return PriceFeedManager.NormalizeSymbol(Symbol);
        }
    }

    private string FormatPrice(decimal rawPrice, string currencySymbol = "$")
    {
        int d = PriceDecimals switch
        {
            "2" => 2, "4" => 4, "6" => 6, "8" => 8,
            _ => rawPrice >= 100 ? 2 : rawPrice >= 1 ? 4 : rawPrice >= 0.01m ? 6 : 8
        };
        return currencySymbol + rawPrice.ToString("N" + d);
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        if (string.IsNullOrWhiteSpace(Symbol))
        {
            DrawPlaceholder(canvas, bounds);
            return;
        }

        AssetKind kind = AssetKindValue;
        if (_lastSubscribedSymbol != Symbol || _lastSubscribedKind != kind)
        {
            _lastSubscribedSymbol = Symbol;
            _lastSubscribedKind = kind;
            _feed.Subscribe(Symbol, kind);
        }

        var info = _feed.GetPrice(Symbol, kind);
        bool isStale = info?.IsStale ?? true;
        if (info != null)
        {
            Price = FormatPrice(info.Price, info.CurrencySymbol);
            ChangeBadge = info.FormattedChange;
        }
        else if ((TimeProvider.System.GetUtcNow().UtcDateTime - _lastFallback).TotalSeconds >= 15)
        {
            _lastFallback = TimeProvider.System.GetUtcNow().UtcDateTime;
            _ = FallbackFetchAsync();
        }

        bool isPositive = info?.IsPositive ?? ChangeBadge.StartsWith('+');
        SKColor textColor = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;
        SKColor posColor = SKColor.TryParse(PositiveColorHex, out var parsedPos) ? parsedPos : new SKColor(34, 197, 94);
        SKColor negColor = SKColor.TryParse(NegativeColorHex, out var parsedNeg) ? parsedNeg : new SKColor(239, 68, 68);

        float pad = 14f;
        float priceSize = Math.Min(bounds.Width / 6f, bounds.Height / 3.5f);

        using var symFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, priceSize);
        using var symPaint = new SKPaint { Color = textColor, IsAntialias = true };
        string symbolText = TextRenderHelper.TruncateText(DisplayLabel, symFont, symPaint, bounds.Width - pad * 2f);
        canvas.DrawText(symbolText, pad, pad + priceSize * 0.8f, SKTextAlign.Left, symFont, symPaint);

        using var priceFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, priceSize);
        using var pricePaint = new SKPaint { Color = textColor, IsAntialias = true };
        canvas.DrawText(Price, pad, bounds.MidY + priceSize * 0.35f, SKTextAlign.Left, priceFont, pricePaint);

        if (ShowChange)
        {
            // Stale prices render in a neutral gray with a freshness dot so the
            // last-known value is never mistaken for live data.
            using var badgeFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, priceSize);
            using var badgePaint = new SKPaint
            {
                Color = isStale ? textColor.WithAlpha(120) : (isPositive ? posColor : negColor),
                IsAntialias = true
            };
            string badgeText = isStale ? $"• {ChangeBadge}" : ChangeBadge;
            canvas.DrawText(badgeText, pad, bounds.Bottom - pad, SKTextAlign.Left, badgeFont, badgePaint);
        }
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds)
    {
        SKColor textColor = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;
        float mainSize = Math.Min(bounds.Width / 6f, bounds.Height / 3.5f);

        using var titleFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, Math.Max(mainSize * 0.55f, 13f));
        using var titlePaint = new SKPaint { Color = textColor, IsAntialias = true };
        TextRenderHelper.DrawCenteredText(canvas, "Enter a symbol", bounds.MidX, bounds.MidY - 4f, titleFont, titlePaint);

        using var hintFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, Math.Max(mainSize * 0.4f, 11f));
        using var hintPaint = new SKPaint { Color = textColor.WithAlpha(160), IsAntialias = true };
        TextRenderHelper.DrawCenteredText(canvas, "e.g. BTC, ETH, AAPL, MSFT", bounds.MidX, bounds.MidY + 16f, hintFont, hintPaint);
    }

    private async Task FallbackFetchAsync()
    {
        try
        {
            if (IsFxAsset) return;
            await _feed.FetchFallbackAsync(Symbol, AssetKindValue);

            var info = _feed.GetPrice(Symbol, AssetKindValue);
            if (info != null)
            {
                Price = FormatPrice(info.Price, info.CurrencySymbol);
                ChangeBadge = info.FormattedChange;
                Context?.RequestRender();
            }
        }
        catch
        {
            System.Diagnostics.Debug.WriteLine("Market price fetch failed; keeping last known price");
        }
    }
}
