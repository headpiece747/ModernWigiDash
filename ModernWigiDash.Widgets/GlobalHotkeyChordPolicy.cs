namespace ModernWigiDash.Widgets;

/// <summary>
/// The global-hotkey chord policy (the chord vocabulary's second owner,
/// beside <see cref="HotkeyActionExecutor.ParseVirtualKey"/>, which owns the
/// key names): parses a stored chord ("Ctrl+Alt+P", the same plus-separated
/// shape the executor validates) into its RegisterHotKey operands. The rules:
/// at least one modifier and exactly one main key (a modifier-less global
/// hotkey would shadow the key system-wide; a modifier-only chord names no
/// key), at most one occurrence of each modifier, unknown keys unparseable.
/// The MOD constants are the Win32 RegisterHotKey vocabulary.
/// </summary>
internal static class GlobalHotkeyChordPolicy
{
    /// <summary>Win32 MOD_ALT.</summary>
    public const int ModAlt = 0x1;
    /// <summary>Win32 MOD_CONTROL.</summary>
    public const int ModControl = 0x2;
    /// <summary>Win32 MOD_SHIFT.</summary>
    public const int ModShift = 0x4;
    /// <summary>Win32 MOD_WIN.</summary>
    public const int ModWin = 0x8;
    /// <summary>Win32 MOD_NOREPEAT (the OS must not autorepeat the hotkey).</summary>
    public const int ModNoRepeat = 0x4000;

    /// <summary>
    /// Parses the stored chord into its RegisterHotKey operands. Returns
    /// false (and leaves the operands zeroed) when the chord is blank, has
    /// no modifier, names no (or two) main keys, repeats a modifier, or
    /// contains an unknown key.
    /// </summary>
    public static bool TryParseChord(string? text, out int modFlags, out ushort virtualKey)
    {
        modFlags = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        int flags = 0;
        ushort? mainKey = null;
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int flag = part.Trim().ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" or "LCONTROL" or "RCONTROL" => ModControl,
                "ALT" or "LALT" or "RALT" => ModAlt,
                "SHIFT" or "LSHIFT" or "RSHIFT" => ModShift,
                "WIN" or "LWIN" or "RWIN" => ModWin,
                _ => 0
            };
            if (flag != 0)
            {
                if ((flags & flag) != 0) return false; // a repeated modifier
                flags |= flag;
                continue;
            }
            if (mainKey is not null) return false; // two main keys
            try
            {
                mainKey = HotkeyActionExecutor.ParseVirtualKey(part);
            }
            catch (ArgumentException)
            {
                return false; // an unknown key
            }
        }

        if (flags == 0 || mainKey is not { } key) return false;
        modFlags = flags;
        virtualKey = key;
        return true;
    }
}
