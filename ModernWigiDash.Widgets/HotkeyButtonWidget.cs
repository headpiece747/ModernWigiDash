using System.Diagnostics;
using System.Runtime.InteropServices;
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
            HotkeyActionKind.MediaKey => $"Media: {MediaKeyCatalog.GetDisplayName(Value) ?? Value}",
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
        ArgumentOutOfRangeException.ThrowIfZero(keys.Length, nameof(text));

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
        ArgumentOutOfRangeException.ThrowIfGreaterThan(text.Length, 4096, nameof(text));
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
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Process.Start(new ProcessStartInfo(path) { Arguments = arguments ?? "", UseShellExecute = true });
    }

    private static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
            throw new ArgumentException("Only http, https, and mailto URLs are allowed.", nameof(url));
        Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
    }

    internal static ushort ParseVirtualKey(string value)
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
            "STOP" => 0xB2,
            "NEXT" => 0xB0,
            "PREVIOUS" or "PREV" => 0xB1,
            _ => throw new ArgumentException($"Unknown key '{value}'.", nameof(value))
        };
    }
}

[WidgetMetadata("hotkey_button", "Hotkey", Description = "Interactive touch button executing macros, shortcuts, or application launches.", Author = "ModernWigiDash", Version = "2.0.0", Category = "Utilities", DefaultGridSize = GridSizePreset.Size1x1)]
public class HotkeyButtonWidget : ModernWidgetBase
{
    public override WidgetSizeMode SizeMode => WidgetSizeMode.Resizable;
    public override SKSize DefaultSize => GridSizePreset.Size1x1.ToSize();

    [WidgetProperty("Button Label", WidgetPropertyType.Text, "Text displayed on button", "Hotkey")]
    public string ButtonLabel { get; set; } = "Hotkey";

    [WidgetProperty("Description", WidgetPropertyType.Text, "Optional secondary text displayed below the button label", "Tap to run")]
    public string Description { get; set; } = "Tap to run";

    [WidgetProperty("Action Type", WidgetPropertyType.Choice, "Trigger action type", "Launch App", "Launch App", "Open URL", "Media Play / Pause", "Media Next", "Media Previous", "Media Stop", "Volume Up", "Volume Down", "Mute")]
    public string ActionType { get; set; } = "Launch App";

    [WidgetProperty("Action Path/Command", WidgetPropertyType.Path, "Executable, file, folder, or URL. You can type a URL or select a local path.", "")]
    public string ActionCommand { get; set; } = "";

    [WidgetProperty("Button Color Hex", WidgetPropertyType.Color, "Button glow accent color", "#F59E0B")]
    public string ButtonColorHex { get; set; } = "#F59E0B";

    [WidgetProperty("Text Color", WidgetPropertyType.Color, "Button label color", "#FAFAFA")]
    public string TextColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Icon", WidgetPropertyType.Icon, "Griddy icon shown above the label (blank = none)", "")]
    public string Icon { get; set; } = "";

    [WidgetProperty("Icon File", WidgetPropertyType.Path, "Custom SVG icon file copied into the icons folder (overrides Icon)", "")]
    public string IconFile { get; set; } = "";

    [WidgetProperty("Icon Color", WidgetPropertyType.Color, "Icon color", "#FAFAFA")]
    public string IconColorHex { get; set; } = "#FAFAFA";

    [WidgetProperty("Icon Size", WidgetPropertyType.Number, "Icon size in px (0 = auto-scale with the widget)", 0)]
    public int IconSize { get; set; } = 0;

    [WidgetProperty("Icon Offset X", WidgetPropertyType.Number, "Horizontal shift of the icon in px (negative = left)", 0)]
    public int IconOffsetX { get; set; } = 0;

    [WidgetProperty("Icon Offset Y", WidgetPropertyType.Number, "Vertical shift of the icon in px (negative = up)", 0)]
    public int IconOffsetY { get; set; } = 0;

    private bool _isPressed = false;
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private CancellationTokenSource? _actionCts;

    /// <summary>
    /// Test seam for action execution. Defaults to
    /// <see cref="HotkeyActionExecutor.ExecuteAsync"/>; tests inject a fake so
    /// the press path (gate, skip, failure logging) can be exercised without
    /// launching processes or sending keys.
    /// </summary>
    public Func<IReadOnlyList<HotkeyAction>, CancellationToken, Task>? ActionExecutor { get; set; }

    public override void Render(SKCanvas canvas, SKRect bounds)
    {
        SKColor btnColor = SKColor.TryParse(ButtonColorHex, out var parsed) ? parsed : new SKColor(135, 0, 0);
        SKColor textColor = SKColor.TryParse(TextColorHex, out var parsedText) ? parsedText : SKColors.White;
        SKColor iconColor = SKColor.TryParse(IconColorHex, out var parsedIcon) ? parsedIcon : SKColors.White;

        if (_isPressed)
        {
            using var fillPaint = new SKPaint
            {
                Color = btnColor.WithAlpha(180),
                IsAntialias = true
            };
            canvas.DrawRoundRect(bounds, 16f, 16f, fillPaint);
        }

        string label = ButtonLabel;

        float maxIconSize = Math.Min(bounds.Width, bounds.Height * 0.62f);
        float iconSize = IconSize > 0 ? IconSize : Math.Min(bounds.Width, bounds.Height) * 0.4f;
        iconSize = Math.Clamp(iconSize, 0f, maxIconSize);
        float half = iconSize / 2f;
        var iconCenter = new SKPoint(
            Math.Clamp(bounds.MidX + IconOffsetX, bounds.Left + half, bounds.Right - half),
            Math.Clamp(bounds.Top + bounds.Height * 0.31f + IconOffsetY, bounds.Top + half, bounds.Bottom - half));

        bool useCustomFile = !string.IsNullOrWhiteSpace(IconFile);
        SKPath? resolvedPath = null;
        bool hasIcon = useCustomFile
            ? SvgIconLoader.TryGetPath(IconFile, out resolvedPath) && resolvedPath != null
            : !string.IsNullOrWhiteSpace(Icon) && GriddyIcons.Contains(Icon);

        if (!hasIcon)
        {
            if (useCustomFile)
                Context?.LogError($"Hotkey custom icon file not found or unsupported: {IconFile}");
            DrawLabelOnly(canvas, bounds, label, textColor, Description);
            return;
        }

        // Draw label and description first so the icon can render in front of them
        float labelSize = Math.Min(bounds.Width / 7f, bounds.Height / 7f);
        using var font = FontHelper.CreateFont("Geist", SKFontStyle.Bold, labelSize);
        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };
        var textBounds = new SKRect();
        font.MeasureText(label, out textBounds, textPaint);
        canvas.DrawText(label, bounds.MidX - textBounds.Width / 2f,
            bounds.Top + bounds.Height * 0.78f, SKTextAlign.Left, font, textPaint);

        if (!string.IsNullOrWhiteSpace(Description))
        {
            using var descriptionFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, Math.Max(10f, labelSize * 0.6f));
            using var descriptionPaint = new SKPaint { Color = textColor.WithAlpha(180), IsAntialias = true };
            descriptionFont.MeasureText(Description, out var descriptionBounds, descriptionPaint);
            canvas.DrawText(Description, bounds.MidX - descriptionBounds.Width / 2f,
                bounds.Bottom - Math.Max(8f, labelSize * 0.4f), SKTextAlign.Left, descriptionFont, descriptionPaint);
        }

        // Icon drawn last so it stays in front of the text when overlapped
        if (useCustomFile)
            SvgIconLoader.Draw(canvas, resolvedPath!, iconCenter, iconSize, iconColor, 0, 0);
        else
            GriddyIcons.Draw(canvas, Icon, iconCenter, iconSize, iconColor, 0, 0);
    }

    private void DrawLabelOnly(SKCanvas canvas, SKRect bounds, string label, SKColor textColor, string description)
    {
        float fontSize = Math.Min(bounds.Width / 6f, bounds.Height / 5f);
        using var font = FontHelper.CreateFont("Geist", SKFontStyle.Bold, fontSize);
        using var textPaint = new SKPaint { Color = textColor, IsAntialias = true };

        var textBounds = new SKRect();
        font.MeasureText(label, out textBounds, textPaint);
        canvas.DrawText(label, bounds.MidX - textBounds.Width / 2f, bounds.MidY - textBounds.Height / 4f, SKTextAlign.Left, font, textPaint);

        if (!string.IsNullOrWhiteSpace(description))
        {
            using var descriptionFont = FontHelper.CreateFont("Geist", SKFontStyle.Normal, Math.Max(10f, fontSize * 0.42f));
            using var descriptionPaint = new SKPaint { Color = textColor.WithAlpha(180), IsAntialias = true };
            descriptionFont.MeasureText(description, out var descriptionBounds, descriptionPaint);
            canvas.DrawText(description, bounds.MidX - descriptionBounds.Width / 2f,
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
        if (_actionCts is { } prior)
        {
            await prior.CancelAsync().ConfigureAwait(false);
            prior.Dispose();
        }
        _actionCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var action = CreateAction(ActionType, ActionCommand);
            if (string.IsNullOrWhiteSpace(action.Value) && IsLaunchOrUrlAction(ActionType))
            {
                Context?.LogError("Hotkey action skipped: Action Path/Command is empty.");
                return;
            }
            var executor = ActionExecutor ?? HotkeyActionExecutor.ExecuteAsync;
            await executor([action], _actionCts.Token).ConfigureAwait(false);
            Context?.RequestRender();
        }
        catch (OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine("Hotkey action cancelled (30s timeout or shutdown)");
        }
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

    internal static HotkeyAction CreateAction(string actionType, string actionCommand)
        => actionType switch
        {
            "Open URL" => new HotkeyAction { Kind = HotkeyActionKind.OpenUrl, Value = actionCommand },
            "Media Play / Pause" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "PLAYPAUSE" },
            "Media Next" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "NEXT" },
            "Media Previous" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "PREVIOUS" },
            "Media Stop" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "STOP" },
            "Volume Up" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "VOLUMEUP" },
            "Volume Down" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "VOLUMEDOWN" },
            "Mute" => new HotkeyAction { Kind = HotkeyActionKind.MediaKey, Value = "MUTE" },
            "Task Manager" => new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = "taskmgr.exe" },
            _ => new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = actionCommand }
        };

    /// <summary>
    /// Single source of truth for 'action needs a command value' (Launch/URL).
    /// The inspector panel and the executor both consult this instead of
    /// re-listing action-type strings.
    /// </summary>
    public static bool IsLaunchOrUrlAction(string actionType)
        => actionType is "Launch App" or "Open URL";

    public override async ValueTask DisposeAsync()
    {
        _actionCts?.Cancel();
        _actionCts?.Dispose();
        _actionGate.Dispose();
        await base.DisposeAsync();
    }
}
