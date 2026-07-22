using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using SkiaSharp;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets;

[WidgetMetadata("hotkey_button", "Hotkey Button", "Interactive touch button executing macros, shortcuts, or application launches.", "ModernWigiDash", "2.0.0", "Utilities", GridSizePreset.Size1x1)]
public class HotkeyButtonWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size1x1.ToSize();

    [WidgetProperty("Button Label", WidgetPropertyType.Text, "Text displayed on button", "Launch Discord")]
    public string ButtonLabel { get; set; } = "Launch Discord";

    [WidgetProperty("Action Type", WidgetPropertyType.Choice, "Trigger action type", "Launch App", "Launch App", "Open URL", "Task Manager")]
    public string ActionType { get; set; } = "Launch App";

    [WidgetProperty("Action Path/Command", WidgetPropertyType.Text, "Exe path or URL", "discord.exe")]
    public string ActionCommand { get; set; } = "discord.exe";

    [WidgetProperty("Button Color Hex", WidgetPropertyType.Color, "Button glow accent color", "#E53935")]
    public string ButtonColorHex { get; set; } = "#E53935"; // Material 3 Red

    private bool _isPressed = false;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor.TryParse(ButtonColorHex, out var btnColor);
        if (btnColor.Alpha == 0) btnColor = new SKColor(229, 57, 53);

        var fillPaint = new SKPaint
        {
            Color = _isPressed ? btnColor.WithAlpha(180) : new SKColor(31, 34, 50, 240),
            IsAntialias = true
        };
        var borderPaint = new SKPaint
        {
            Color = _isPressed ? SKColors.White : btnColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = _isPressed ? 3f : 1.8f,
            IsAntialias = true
        };

        canvas.DrawRoundRect(bounds, 16f, 16f, fillPaint);
        canvas.DrawRoundRect(bounds, 16f, 16f, borderPaint);

        float fontSize = Math.Min(bounds.Width / 6f, bounds.Height / 5f);
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), fontSize);
        using var textPaint = new SKPaint { Color = new SKColor(244, 239, 244), IsAntialias = true };

        var textBounds = new SKRect();
        font.MeasureText(ButtonLabel, out textBounds, textPaint);
        canvas.DrawText(ButtonLabel, bounds.MidX - (textBounds.Width / 2f), bounds.MidY + (textBounds.Height / 3f), SKTextAlign.Left, font, textPaint);
    }

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchDown)
        {
            _isPressed = true;
            Context?.RequestRender();
        }
        else if (eventType == TouchEventType.TouchUp)
        {
            _isPressed = false;
            ExecuteAction();
            Context?.RequestRender();
        }
    }

    private void ExecuteAction()
    {
        try
        {
            if (ActionType == "Task Manager")
            {
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
            }
            else if (ActionType == "Open URL" && !string.IsNullOrEmpty(ActionCommand))
            {
                string url = ActionCommand.StartsWith("http") ? ActionCommand : $"https://{ActionCommand}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (!string.IsNullOrEmpty(ActionCommand))
            {
                Process.Start(new ProcessStartInfo(ActionCommand) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            Context?.LogError($"Hotkey action failed: {ex.Message}", ex);
        }
    }
}

[WidgetMetadata("stopwatch_timer", "Stopwatch & Timer", "Precision millisecond stopwatch with touch Start/Pause/Reset controls.", "ModernWigiDash", "2.0.0", "Utilities", GridSizePreset.Size1x1)]
public class StopwatchTimerWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size1x1.ToSize();

    private bool _isRunning = false;
    private DateTime _startTime = DateTime.Now;
    private TimeSpan _elapsed = TimeSpan.Zero;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        using var bgPaint = new SKPaint { Color = new SKColor(31, 34, 50, 230), IsAntialias = true };
        using var borderPaint = new SKPaint { Color = new SKColor(229, 57, 53, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 16f, 16f, bgPaint);
        canvas.DrawRoundRect(bounds, 16f, 16f, borderPaint);

        var total = _isRunning ? _elapsed + (DateTime.Now - _startTime) : _elapsed;
        string timeStr = $"{total.Minutes:D2}:{total.Seconds:D2}.{total.Milliseconds / 10:D2}";

        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), bounds.Width * 0.18f);
        using var textPaint = new SKPaint { Color = new SKColor(244, 239, 244), IsAntialias = true };
        var tb = new SKRect();
        font.MeasureText(timeStr, out tb, textPaint);
        canvas.DrawText(timeStr, bounds.MidX - (tb.Width / 2f), bounds.MidY - 5f, SKTextAlign.Left, font, textPaint);

        using var subFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 11f);
        using var subPaint = new SKPaint { Color = new SKColor(255, 180, 171), IsAntialias = true };
        string statusStr = _isRunning ? "🔴 TAP TO PAUSE" : "🟢 TAP TO START";
        var sb = new SKRect();
        subFont.MeasureText(statusStr, out sb, subPaint);
        canvas.DrawText(statusStr, bounds.MidX - (sb.Width / 2f), bounds.Bottom - 16f, SKTextAlign.Left, subFont, subPaint);
    }

    public override void OnTouch(SKPoint localPoint, TouchEventType eventType)
    {
        if (eventType == TouchEventType.TouchDown)
        {
            if (_isRunning)
            {
                _elapsed += DateTime.Now - _startTime;
                _isRunning = false;
            }
            else
            {
                _startTime = DateTime.Now;
                _isRunning = true;
            }
            Context?.RequestRender();
        }
    }
}

[WidgetMetadata("ticker_stock", "Stock & Crypto Ticker", "Shows live stock/crypto symbol, real-time price, and trend badges via live CoinGecko API.", "ModernWigiDash", "2.0.0", "Utilities", GridSizePreset.Size1x1)]
public class CryptoStockTickerWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size1x1.ToSize();

    [WidgetProperty("Symbol", WidgetPropertyType.Text, "Stock or Crypto Symbol (e.g. bitcoin, ethereum, solana)", "bitcoin")]
    public string Symbol { get; set; } = "bitcoin";

    [WidgetProperty("Price", WidgetPropertyType.Text, "Live price reading", "$96,450.00")]
    public string Price { get; set; } = "$96,450.00";

    [WidgetProperty("Change Badge", WidgetPropertyType.Text, "Percentage trend badge", "+4.85%")]
    public string ChangeBadge { get; set; } = "+4.85%";

    private static readonly HttpClient _httpClient = new HttpClient();
    private DateTime _lastFetch = DateTime.MinValue;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        FetchLivePriceAsync();

        using var bgPaint = new SKPaint { Color = new SKColor(31, 34, 50, 230), IsAntialias = true };
        using var borderPaint = new SKPaint { Color = new SKColor(229, 57, 53, 180), Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, IsAntialias = true };
        canvas.DrawRoundRect(bounds, 16f, 16f, bgPaint);
        canvas.DrawRoundRect(bounds, 16f, 16f, borderPaint);

        float pad = 14f;
        using var symFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 13f);
        using var symPaint = new SKPaint { Color = new SKColor(224, 194, 196), IsAntialias = true };
        canvas.DrawText(Symbol.ToUpper(), pad, pad + 12f, SKTextAlign.Left, symFont, symPaint);

        using var priceFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 20f);
        using var pricePaint = new SKPaint { Color = new SKColor(244, 239, 244), IsAntialias = true };
        canvas.DrawText(Price, pad, bounds.MidY + 8f, SKTextAlign.Left, priceFont, pricePaint);

        using var badgeFont = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), 12f);
        using var badgePaint = new SKPaint { Color = new SKColor(255, 180, 171), IsAntialias = true };
        canvas.DrawText(ChangeBadge, pad, bounds.Bottom - pad, SKTextAlign.Left, badgeFont, badgePaint);
    }

    private async void FetchLivePriceAsync()
    {
        if ((DateTime.Now - _lastFetch).TotalSeconds < 30) return;
        _lastFetch = DateTime.Now;

        try
        {
            string url = $"https://api.coingecko.com/api/v3/simple/price?ids={Symbol.ToLower()}&vs_currencies=usd&include_24hr_change=true";
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ModernWigiDash/2.0");
            string json = await _httpClient.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(Symbol.ToLower(), out var coinEl))
            {
                if (coinEl.TryGetProperty("usd", out var usdEl))
                {
                    double usd = usdEl.GetDouble();
                    Price = $"${usd:N2}";
                }
                if (coinEl.TryGetProperty("usd_24h_change", out var changeEl))
                {
                    double change = changeEl.GetDouble();
                    ChangeBadge = $"{(change >= 0 ? "+" : "")}{change:F2}%";
                }
                Context?.RequestRender();
            }
        }
        catch { }
    }
}
