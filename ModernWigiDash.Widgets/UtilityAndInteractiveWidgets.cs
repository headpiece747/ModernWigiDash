using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModernWigiDash.Sdk;
using SkiaSharp;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.Widgets;

public enum HotkeyActionKind
{
    KeyChord,
    Text,
    MouseClick,
    MouseDoubleClick,
    MouseWheel,
    Delay,
    Launch,
    OpenUrl,
    MediaKey
}

public sealed class HotkeyAction
{
    public HotkeyActionKind Kind { get; set; } = HotkeyActionKind.KeyChord;
    public string Value { get; set; } = "Ctrl+Shift+S";
    public string Arguments { get; set; } = "";
    public int DelayMs { get; set; } = 20;
    public int Repeat { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    public string Summary()
        => Kind switch
        {
            HotkeyActionKind.Launch => $"Launch {Value}",
            HotkeyActionKind.OpenUrl => $"Open {Value}",
            HotkeyActionKind.Delay => $"Wait {Math.Max(0, DelayMs)} ms",
            _ => $"{Kind}: {Value}"
        };
}

internal static class HotkeyActionExecutor
{
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;
    private const uint MouseWheel = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    public static async Task ExecuteAsync(IReadOnlyList<HotkeyAction> actions, CancellationToken cancellationToken)
    {
        if (actions.Count > 64) throw new InvalidOperationException("A macro cannot contain more than 64 actions.");

        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!action.Enabled) continue;

            int repeat = Math.Clamp(action.Repeat, 1, 20);
            for (int i = 0; i < repeat; i++)
            {
                await ExecuteOneAsync(action, cancellationToken).ConfigureAwait(false);
                if (action.DelayMs > 0)
                    await Task.Delay(Math.Clamp(action.DelayMs, 0, 5000), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteOneAsync(HotkeyAction action, CancellationToken cancellationToken)
    {
        switch (action.Kind)
        {
            case HotkeyActionKind.KeyChord:
                SendChord(action.Value);
                break;
            case HotkeyActionKind.Text:
                SendUnicodeText(action.Value);
                break;
            case HotkeyActionKind.MouseClick:
                SendMouseClick(action.Value);
                break;
            case HotkeyActionKind.MouseDoubleClick:
                SendMouseClick(action.Value);
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                SendMouseClick(action.Value);
                break;
            case HotkeyActionKind.MouseWheel:
                SendMouseWheel(action.Value);
                break;
            case HotkeyActionKind.Delay:
                await Task.Delay(Math.Clamp(action.DelayMs, 0, 5000), cancellationToken).ConfigureAwait(false);
                break;
            case HotkeyActionKind.Launch:
                Launch(action.Value, action.Arguments);
                break;
            case HotkeyActionKind.OpenUrl:
                OpenUrl(action.Value);
                break;
            case HotkeyActionKind.MediaKey:
                SendChord(action.Value);
                break;
        }
    }

    private static void SendChord(string text)
    {
        var keys = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseVirtualKey).ToArray();
        if (keys.Length == 0) throw new ArgumentException("Enter a key or key chord.", nameof(text));

        try
        {
            SendKeys(keys, keyUp: false);
        }
        finally
        {
            SendKeys(keys.Reverse().ToArray(), keyUp: true);
        }
    }

    private static void SendKeys(IEnumerable<ushort> keys, bool keyUp)
    {
        var inputs = keys.Select(key => new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = key,
                    Flags = keyUp ? KeyEventKeyUp : 0
                }
            }
        }).ToArray();

        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new InvalidOperationException($"Windows rejected the keyboard input ({Marshal.GetLastWin32Error()}).");
    }

    private static void SendUnicodeText(string text)
    {
        if (text.Length > 4096) throw new ArgumentException("Text action is limited to 4096 characters.", nameof(text));
        var inputs = new List<Input>(text.Length * 2);
        foreach (char character in text)
        {
            inputs.Add(new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyEventUnicode } } });
            inputs.Add(new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyEventUnicode | KeyEventKeyUp } } });
        }
        if (inputs.Count > 0 && SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>()) != inputs.Count)
            throw new InvalidOperationException($"Windows rejected the text input ({Marshal.GetLastWin32Error()}).");
    }

    private static void SendMouseClick(string button)
    {
        (uint down, uint up) = button.Trim().ToLowerInvariant() switch
        {
            "right" or "rbutton" => (MouseRightDown, MouseRightUp),
            "middle" or "mbutton" => (MouseMiddleDown, MouseMiddleUp),
            _ => (MouseLeftDown, MouseLeftUp)
        };
        SendMouse(down);
        SendMouse(up);
    }

    private static void SendMouseWheel(string direction)
    {
        int amount = int.TryParse(direction, out int value) ? value : direction.Trim().Equals("down", StringComparison.OrdinalIgnoreCase) ? -120 : 120;
        SendMouse(MouseWheel, unchecked((uint)amount));
    }

    private static void SendMouse(uint flags, uint data = 0)
    {
        var input = new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = data } } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException($"Windows rejected the mouse input ({Marshal.GetLastWin32Error()}).");
    }

    private static void Launch(string path, string arguments)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Launch path is required.", nameof(path));
        Process.Start(new ProcessStartInfo(path) { Arguments = arguments ?? "", UseShellExecute = true });
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
            throw new ArgumentException("Only http, https, and mailto URLs are allowed.", nameof(url));
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    private static ushort ParseVirtualKey(string value)
    {
        string key = value.Trim().ToUpperInvariant();
        if (key.Length == 1 && ((key[0] is >= 'A' and <= 'Z') || (key[0] is >= '0' and <= '9')))
            return key[0];
        if (key.StartsWith('F') && int.TryParse(key[1..], out int function) && function is >= 1 and <= 24)
            return (ushort)(0x70 + function - 1);

        return key switch
        {
            "CTRL" or "CONTROL" or "LCONTROL" => 0xA2,
            "RCONTROL" => 0xA3,
            "ALT" or "LALT" => 0xA4,
            "RALT" => 0xA5,
            "SHIFT" or "LSHIFT" => 0xA0,
            "RSHIFT" => 0xA1,
            "WIN" or "LWIN" => 0x5B,
            "RWIN" => 0x5C,
            "ENTER" or "RETURN" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "TAB" => 0x09,
            "SPACE" => 0x20,
            "BACKSPACE" => 0x08,
            "DELETE" or "DEL" => 0x2E,
            "INSERT" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" => 0x21,
            "PAGEDOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "VOLUMEUP" => 0xAF,
            "VOLUMEDOWN" => 0xAE,
            "MUTE" => 0xAD,
            "PLAYPAUSE" => 0xB3,
            "NEXT" => 0xB0,
            "PREVIOUS" or "PREV" => 0xB1,
            _ => throw new ArgumentException($"Unknown key '{value}'.", nameof(value))
        };
    }
}

[WidgetMetadata("hotkey_button", "Hotkey", "Interactive touch button executing macros, shortcuts, or application launches.", "ModernWigiDash", "2.0.0", "Utilities", GridSizePreset.Size1x1)]
public class HotkeyButtonWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size1x1.ToSize();

    [WidgetProperty("Button Label", WidgetPropertyType.Text, "Text displayed on button", "Hotkey")]
    public string ButtonLabel { get; set; } = "Hotkey";

    [WidgetProperty("Description", WidgetPropertyType.Text, "Optional secondary text displayed below the button label", "Tap to run")]
    public string Description { get; set; } = "Tap to run";

    [WidgetProperty("Action Type", WidgetPropertyType.Choice, "Trigger action type", "Launch App", "Launch App", "Open URL", "Task Manager")]
    public string ActionType { get; set; } = "Launch App";

    [WidgetProperty("Action Path/Command", WidgetPropertyType.Path, "Executable, file, folder, or URL. You can type a URL or select a local path.", "")]
    public string ActionCommand { get; set; } = "";

    [WidgetProperty("Button Color Hex", WidgetPropertyType.Color, "Button glow accent color", "#F59E0B")]
    public string ButtonColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Button label color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Toggle Actions", WidgetPropertyType.Boolean, "Run the toggled action list after the first press", false)]
    public bool ToggleActions { get; set; }

    [WidgetProperty("Toggled Button Label", WidgetPropertyType.Text, "Label shown while toggled", "Active")]
    public string ToggledButtonLabel { get; set; } = "Active";

    [WidgetProperty("Actions", WidgetPropertyType.ActionList, "Actions run in order on the normal state")]
    public List<HotkeyAction> Actions { get; set; } = [];

    [WidgetProperty("Toggled Actions", WidgetPropertyType.ActionList, "Actions run in order on the toggled state")]
    public List<HotkeyAction> ToggledActions { get; set; } = [];

    private bool _isPressed = false;
    private bool _isToggled;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private CancellationTokenSource? _actionCts;

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor btnColor = SKColor.TryParse(ButtonColorHex, out var parsed) ? parsed : new SKColor(135, 0, 0);
        SKColor textColor = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;

        var fillPaint = new SKPaint
        {
            Color = _isPressed ? btnColor.WithAlpha(180) : SKColors.Transparent,
            IsAntialias = true
        };

        if (_isPressed)
        {
            canvas.DrawRoundRect(bounds, 16f, 16f, fillPaint);
        }

        float fontSize = Math.Min(bounds.Width / 6f, bounds.Height / 5f);
        using var font = FontHelper.CreateFont("Geist", SKFontStyle.Bold, fontSize);
        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };

        var textBounds = new SKRect();
        string label = _isToggled && ToggleActions ? ToggledButtonLabel : ButtonLabel;
        font.MeasureText(label, out textBounds, textPaint);
        canvas.DrawText(label, bounds.MidX - (textBounds.Width / 2f), bounds.MidY - (textBounds.Height / 4f), SKTextAlign.Left, font, textPaint);

        if (!string.IsNullOrWhiteSpace(Description))
        {
            using var descriptionFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, Math.Max(10f, fontSize * 0.42f));
            using var descriptionPaint = new SKPaint { Color = textColor.WithAlpha(180), IsAntialias = true };
            descriptionFont.MeasureText(Description, out var descriptionBounds, descriptionPaint);
            canvas.DrawText(Description, bounds.MidX - descriptionBounds.Width / 2f,
                bounds.Bottom - Math.Max(12f, fontSize * 0.65f), SKTextAlign.Left, descriptionFont, descriptionPaint);
        }
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
            _ = ExecuteActionsAsync();
            Context?.RequestRender();
        }
    }

    private async Task ExecuteActionsAsync()
    {
        if (!await _actionGate.WaitAsync(0).ConfigureAwait(false)) return;
        _actionCts?.Cancel();
        _actionCts?.Dispose();
        _actionCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var actions = (ToggleActions && _isToggled) ? ToggledActions : Actions;
            if (actions.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(ActionCommand)) return;
                actions = [CreateLegacyAction()];
            }
            await HotkeyActionExecutor.ExecuteAsync(actions, _actionCts.Token).ConfigureAwait(false);
            if (ToggleActions) _isToggled = !_isToggled;
            Context?.RequestRender();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Context?.LogError($"Hotkey action failed: {ex.Message}", ex);
        }
        finally
        {
            _actionCts.Dispose();
            _actionCts = null;
            _actionGate.Release();
        }
    }

    private HotkeyAction CreateLegacyAction()
        => ActionType switch
        {
            "Open URL" => new HotkeyAction { Kind = HotkeyActionKind.OpenUrl, Value = ActionCommand },
            "Task Manager" => new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = "taskmgr.exe" },
            _ => new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = ActionCommand }
        };

    public override async ValueTask DisposeAsync()
    {
        _actionCts?.Cancel();
        _actionCts?.Dispose();
        _actionGate.Dispose();
        await base.DisposeAsync();
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

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Timer digits color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Accent Color", WidgetPropertyType.Color, "Status label color", "#F59E0B")]
    public string AccentColorHex { get; set; } = "#F59E0B";

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        var total = _isRunning ? _elapsed + (DateTime.Now - _startTime) : _elapsed;
        string timeStr = $"{total.Minutes:D2}:{total.Seconds:D2}.{total.Milliseconds / 10:D2}";
        SKColor textColor = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;
        SKColor accentColor = SKColor.TryParse(AccentColorHex, out var parsedAccent) ? parsedAccent : SKColors.White;

        using var font = FontHelper.CreateFont("Geist", SKFontStyle.Bold, bounds.Width * 0.18f);
        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };
        var tb = new SKRect();
        font.MeasureText(timeStr, out tb, textPaint);
        canvas.DrawText(timeStr, bounds.MidX - (tb.Width / 2f), bounds.MidY - 5f, SKTextAlign.Left, font, textPaint);

        using var subFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, 11f);
        using var subPaint = new SKPaint { Color = accentColor, IsAntialias = true };
        string statusStr = _isRunning ? "TAP TO PAUSE" : "TAP TO START";
        var sb = new SKRect();
        subFont.MeasureText(statusStr, out sb, subPaint);
        float dotR = 4f;
        float dotX = bounds.MidX - (sb.Width / 2f) - dotR * 2f - 5f;
        float dotY = bounds.Bottom - 16f - 4f;
        using var dotPaint = new SKPaint { Color = _isRunning ? new SKColor(239, 68, 68) : new SKColor(34, 197, 94), IsAntialias = true };
        canvas.DrawCircle(dotX, dotY, dotR, dotPaint);
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

[WidgetMetadata("ticker_stock", "Stock & Crypto", "Shows live stock/crypto symbol, real-time price, and trend badges via WebSocket.", "ModernWigiDash", "2.0.0", "Utilities", GridSizePreset.Size1x1)]
public class CryptoStockTickerWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size1x1.ToSize();

    [WidgetProperty("Symbol", WidgetPropertyType.Text, "Crypto name (bitcoin, solana) or stock ticker (AAPL, MSFT)")]
    public string Symbol { get; set; } = "";

    [WidgetProperty("Asset Type", WidgetPropertyType.Choice, "Force type when auto-detection doesn't recognize your symbol", "Auto", "Auto", "Crypto", "Stock")]
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
    private static readonly HttpClient _httpClient = new HttpClient();
    private string? _lastSubscribedSymbol;
    private bool _lastSubscribedIsCrypto;
    private DateTime _lastFallback = DateTime.MinValue;

    private bool IsCryptoAsset => AssetType == "Crypto" || (AssetType == "Auto" && PriceFeedManager.IsCrypto(Symbol));

    private string DisplayLabel => !string.IsNullOrEmpty(DisplayName)
        ? DisplayName
        : PriceFeedManager.NormalizeSymbol(Symbol);

    private string FormatPrice(decimal rawPrice)
    {
        int d = PriceDecimals switch
        {
            "2" => 2, "4" => 4, "6" => 6, "8" => 8,
            _ => rawPrice >= 100 ? 2 : rawPrice >= 1 ? 4 : rawPrice >= 0.01m ? 6 : 8
        };
        return "$" + rawPrice.ToString("N" + d);
    }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        if (string.IsNullOrWhiteSpace(Symbol))
        {
            DrawPlaceholder(canvas, bounds);
            return;
        }

        bool isCrypto = IsCryptoAsset;
        if (_lastSubscribedSymbol != Symbol || _lastSubscribedIsCrypto != isCrypto)
        {
            _lastSubscribedSymbol = Symbol;
            _lastSubscribedIsCrypto = isCrypto;
            _feed.Subscribe(Symbol, isCrypto);
        }

        var info = _feed.GetPrice(Symbol, isCrypto);
        if (info != null)
        {
            Price = FormatPrice(info.Price);
            ChangeBadge = info.FormattedChange;
        }
        else if ((DateTime.Now - _lastFallback).TotalSeconds >= 15)
        {
            _lastFallback = DateTime.Now;
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
        string symbolText = TruncateToFit(DisplayLabel, symFont, symPaint, bounds.Width - pad * 2f);
        canvas.DrawText(symbolText, pad, pad + priceSize * 0.8f, SKTextAlign.Left, symFont, symPaint);

        using var priceFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, priceSize);
        using var pricePaint = new SKPaint { Color = textColor, IsAntialias = true };
        canvas.DrawText(Price, pad, bounds.MidY + priceSize * 0.35f, SKTextAlign.Left, priceFont, pricePaint);

        if (ShowChange)
        {
            using var badgeFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, priceSize);
            using var badgePaint = new SKPaint { Color = isPositive ? posColor : negColor, IsAntialias = true };
            canvas.DrawText(ChangeBadge, pad, bounds.Bottom - pad, SKTextAlign.Left, badgeFont, badgePaint);
        }
    }

    private void DrawPlaceholder(SKCanvas canvas, SKRect bounds)
    {
        SKColor textColor = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;
        float mainSize = Math.Min(bounds.Width / 6f, bounds.Height / 3.5f);

        using var titleFont = FontHelper.CreateFont("Geist", SKFontStyle.Bold, Math.Max(mainSize * 0.55f, 13f));
        using var titlePaint = new SKPaint { Color = textColor, IsAntialias = true };
        string title = "Enter a symbol";
        float titleW = titleFont.MeasureText(title);
        canvas.DrawText(title, bounds.MidX - titleW / 2f, bounds.MidY - 4f, SKTextAlign.Left, titleFont, titlePaint);

        using var hintFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, Math.Max(mainSize * 0.4f, 11f));
        using var hintPaint = new SKPaint { Color = textColor.WithAlpha(160), IsAntialias = true };
        string hint = "e.g. BTC, ETH, AAPL, MSFT";
        float hintW = hintFont.MeasureText(hint);
        canvas.DrawText(hint, bounds.MidX - hintW / 2f, bounds.MidY + 16f, SKTextAlign.Left, hintFont, hintPaint);
    }

    private static string TruncateToFit(string text, SKFont font, SKPaint paint, float maxWidth)
    {
        if (font.MeasureText(text, paint) <= maxWidth) return text;

        const string ellipsis = "…";
        string truncated = text;
        while (truncated.Length > 1 && font.MeasureText(truncated + ellipsis, paint) > maxWidth)
        {
            truncated = truncated[..^1];
        }
        return truncated + ellipsis;
    }

    private async Task FallbackFetchAsync()
    {
        try
        {
            if (IsCryptoAsset)
            {
                string url = $"https://api.coingecko.com/api/v3/simple/price?ids={Symbol.ToLower()}&vs_currencies=usd&include_24hr_change=true";
                string json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty(Symbol.ToLower(), out var coinEl))
                {
                    if (coinEl.TryGetProperty("usd", out var usdEl))
                        Price = FormatPrice((decimal)usdEl.GetDouble());
                    if (coinEl.TryGetProperty("usd_24h_change", out var changeEl))
                        ChangeBadge = $"{(changeEl.GetDouble() >= 0 ? "+" : "")}{changeEl.GetDouble():F2}%";
                    Context?.RequestRender();
                }
            }
            else
            {
                string url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Symbol.ToUpper()}?interval=1d&range=1d";
                string json = await _httpClient.GetStringAsync(url);
                using var doc = JsonDocument.Parse(json);
                var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
                var meta = result.GetProperty("meta");
                decimal price = (decimal)meta.GetProperty("regularMarketPrice").GetDouble();
                decimal prevClose = (decimal)meta.GetProperty("chartPreviousClose").GetDouble();
                decimal change = price - prevClose;
                decimal changePct = (change / prevClose) * 100;
                Price = FormatPrice(price);
                ChangeBadge = $"{(change >= 0 ? "+" : "")}{changePct:F2}%";
                Context?.RequestRender();
            }
        }
        catch
        {
        }
    }
}
