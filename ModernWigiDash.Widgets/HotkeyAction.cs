namespace ModernWigiDash.Widgets;

/// <summary>The kind of macro step a <see cref="HotkeyAction"/> performs.</summary>
internal enum HotkeyActionKind
{
    KeyChord,
    Text,
    MouseClick,
    MouseDoubleClick,
    MouseWheel,
    Delay,
    Launch,
    OpenUrl,
    MediaKey,
    PageNavigate,
    AhkScript
}

/// <summary>
/// One macro step of a hotkey button: the kind of input plus its value,
/// timing, and enablement. Serialized with the profile, so the shape is
/// stable. Summary is the human-readable description shown in the inspector.
/// </summary>
internal sealed class HotkeyAction
{
    public HotkeyActionKind Kind { get; set; } = HotkeyActionKind.KeyChord;
    public string Value { get; set; } = "Ctrl+Shift+S";
    public string Arguments { get; set; } = "";
    public int DelayMs { get; set; } = 20;
    public int Repeat { get; set; } = 1;
    public bool Enabled { get; set; } = true;

    internal string Summary()
        => Kind switch
        {
            HotkeyActionKind.Launch => $"Launch {Value}",
            HotkeyActionKind.OpenUrl => $"Open {Value}",
            HotkeyActionKind.Delay => $"Wait {Math.Max(0, DelayMs)} ms",
            HotkeyActionKind.MediaKey => $"Media: {MediaKeyCatalog.GetDisplayName(Value) ?? Value}",
            HotkeyActionKind.PageNavigate => string.Equals(Value, "-1", StringComparison.Ordinal) ? "Previous page" : "Next page",
            HotkeyActionKind.AhkScript => $"Run AHK {Value}",
            _ => $"{Kind}: {Value}"
        };
}
