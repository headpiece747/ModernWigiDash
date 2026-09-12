using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Executes a hotkey macro against the Windows input subsystem (SendInput) —
/// key chords, unicode text, mouse clicks/wheel, delays, launches, and URL
/// opening. Seam behind <see cref="HotkeyButtonWidget.ActionExecutor"/>.
/// </summary>
internal static class HotkeyActionExecutor
{
    private const uint InputKeyboard = 1;
    private const uint InputMouse = 0;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

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

    /// <summary>The OS surface the executor routes its input sends and process
    /// starts through. Defaults to the production binding (real SendInput +
    /// Process.Start); tests inject a fake so the routing is assertable without
    /// sending real keystrokes or spawning processes.</summary>
    internal static HotkeyActionApi Api { get; set; } = HotkeyActionApi.Default;

    public static async Task ExecuteAsync(IReadOnlyList<HotkeyAction> actions, CancellationToken cancellationToken)
    {
        if (actions.Count > HotkeyActionPolicy.MaxActions) throw new InvalidOperationException($"A macro cannot contain more than {HotkeyActionPolicy.MaxActions} actions.");

        foreach (var action in actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!action.Enabled) continue;

            int repeat = HotkeyActionPolicy.ClampRepeat(action.Repeat);
            for (int i = 0; i < repeat; i++)
            {
                await ExecuteOneAsync(action, cancellationToken).ConfigureAwait(false);
                if (action.DelayMs > 0)
                    await Task.Delay(HotkeyActionPolicy.ClampDelayMs(action.DelayMs), cancellationToken).ConfigureAwait(false);
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
                await Task.Delay(HotkeyActionPolicy.ClampDelayMs(action.DelayMs), cancellationToken).ConfigureAwait(false);
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
        if (keys.Length == 0) throw new ArgumentOutOfRangeException(nameof(text), "A key chord must contain at least one key.");

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

        var pinned = GCHandle.Alloc(inputs, GCHandleType.Pinned);
        try
        {
            if (Api.SendInput((uint)inputs.Length, pinned.AddrOfPinnedObject(), Marshal.SizeOf<Input>()) != inputs.Length)
                throw new InvalidOperationException($"Windows rejected the keyboard input.");
        }
        finally
        {
            pinned.Free();
        }
    }

    private static void SendUnicodeText(string text)
    {
        if (text.Length > HotkeyActionPolicy.MaxTextLength) throw new ArgumentOutOfRangeException(nameof(text), $"Text exceeds the {HotkeyActionPolicy.MaxTextLength}-character limit.");
        var inputs = new List<Input>(text.Length * 2);
        foreach (char character in text)
        {
            inputs.Add(new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyEventUnicode } } });
            inputs.Add(new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyEventUnicode | KeyEventKeyUp } } });
        }
        if (inputs.Count > 0)
        {
            var array = inputs.ToArray();
            var pinned = GCHandle.Alloc(array, GCHandleType.Pinned);
            try
            {
                if (Api.SendInput((uint)array.Length, pinned.AddrOfPinnedObject(), Marshal.SizeOf<Input>()) != array.Length)
                    throw new InvalidOperationException($"Windows rejected the text input.");
            }
            finally
            {
                pinned.Free();
            }
        }
    }

    private static void SendMouseClick(string button)
    {
        (uint down, uint up) = HotkeyActionPolicy.MouseButtonFlags(button);
        SendMouse(down);
        SendMouse(up);
    }

    private static void SendMouseWheel(string direction)
    {
        int amount = HotkeyActionPolicy.WheelAmount(direction);
        SendMouse(HotkeyActionPolicy.WheelFlag, unchecked((uint)amount));
    }

    private static void SendMouse(uint flags, uint data = 0)
    {
        var input = new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = flags, MouseData = data } } };
        Input[] array = [input];
        var pinned = GCHandle.Alloc(array, GCHandleType.Pinned);
        try
        {
            if (Api.SendInput(1, pinned.AddrOfPinnedObject(), Marshal.SizeOf<Input>()) != 1)
                throw new InvalidOperationException($"Windows rejected the mouse input.");
        }
        finally
        {
            pinned.Free();
        }
    }

    private static void Launch(string path, string arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Api.StartProcess(path, arguments ?? "");
    }

    private static void OpenUrl(string url)
    {
        if (!ShellOpenPolicy.IsAllowedUrl(url))
            throw new ArgumentException("Only http, https, and mailto URLs are allowed.", nameof(url));
        Api.StartProcess(url, "");
    }

    internal static ushort ParseVirtualKey(string value)
    {
        string key = value.Trim().ToUpperInvariant();
        if (MediaKeyCatalog.TryGetVirtualKey(key, out ushort mediaVk))
        {
            return mediaVk;
        }
        if (key.Length == 1 && ((key[0] is >= 'A' and <= 'Z') || (key[0] is >= '0' and <= '9')))
            return key[0];
        if (key.StartsWith('F') && int.TryParse(key[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int function) && function is >= 1 and <= 24)
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
            _ => throw new ArgumentException($"Unknown key '{value}'.", nameof(value))
        };
    }
}
