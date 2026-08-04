namespace ModernWigiDash.Widgets;

public sealed record MediaKeyOption(string Value, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public static class MediaKeyCatalog
{
    public static readonly IReadOnlyList<MediaKeyOption> Options =
    [
        new("PLAYPAUSE", "Play / Pause"),
        new("NEXT", "Next track"),
        new("PREVIOUS", "Previous track"),
        new("STOP", "Stop"),
        new("VOLUMEUP", "Volume up"),
        new("VOLUMEDOWN", "Volume down"),
        new("MUTE", "Mute")
    ];

    public static string? GetDisplayName(string value)
        => Options.FirstOrDefault(o => o.Value.Equals(value, StringComparison.OrdinalIgnoreCase))?.DisplayName;
}
