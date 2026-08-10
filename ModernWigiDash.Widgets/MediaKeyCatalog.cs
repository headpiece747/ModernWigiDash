namespace ModernWigiDash.Widgets;

internal sealed record MediaKeyOption(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// The single owner of the media-key tokens: the token strings, their VK
/// codes, and their display names. The action builder, the executor's key
/// parser, and the picker all reference the constants here — a token exists
/// in exactly one place.
/// </summary>
internal static class MediaKeyCatalog
{
    public const string PlayPause = "PLAYPAUSE";
    public const string Next = "NEXT";
    public const string Previous = "PREVIOUS";
    public const string Stop = "STOP";
    public const string VolumeUp = "VOLUMEUP";
    public const string VolumeDown = "VOLUMEDOWN";
    public const string Mute = "MUTE";

    internal static readonly IReadOnlyList<MediaKeyOption> Options =
    [
        new(PlayPause, "Play / Pause"),
        new(Next, "Next track"),
        new(Previous, "Previous track"),
        new(Stop, "Stop"),
        new(VolumeUp, "Volume up"),
        new(VolumeDown, "Volume down"),
        new(Mute, "Mute")
    ];

    internal static string? GetDisplayName(string value)
        => Options.FirstOrDefault(o => o.Value.Equals(value, StringComparison.OrdinalIgnoreCase))?.DisplayName;

    /// <summary>
    /// The SendInput virtual-key code for a media token ("PREV" accepted as an
    /// alias of PREVIOUS); false for non-media keys.
    /// </summary>
    internal static bool TryGetVirtualKey(string value, out ushort virtualKey)
    {
        string key = value.Trim().ToUpperInvariant();
        virtualKey = key switch
        {
            VolumeUp => 0xAF,
            VolumeDown => 0xAE,
            Mute => 0xAD,
            PlayPause => 0xB3,
            Stop => 0xB2,
            Next => 0xB0,
            Previous or "PREV" => 0xB1,
            _ => 0,
        };
        return virtualKey != 0;
    }
}
