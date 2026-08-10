namespace ModernWigiDash.Widgets.Twitch;

/// <summary>
/// Pure display rules for the Twitch chat widget: the header status line and
/// the empty-state hint, previously composed inline in the render path and
/// never asserted.
/// </summary>
public static class TwitchChatPresentation
{
    /// <summary>The header status line: a state dot plus the detail (or the state's default).</summary>
    public static string StatusText(ChatStatus status, string detail)
        => status switch
        {
            ChatStatus.Connected => "● " + (detail.Length > 0 ? detail : "LIVE"),
            ChatStatus.Connecting => "⟳ " + (detail.Length > 0 ? detail : "Connecting…"),
            ChatStatus.Disconnected => "○ " + (detail.Length > 0 ? detail : "Disconnected"),
            _ => "○ Disconnected" // unreachable — guards undefined enum values
        };

    /// <summary>The empty-chat hint shown when no messages have arrived yet.</summary>
    public static string EmptyHint(ChatStatus status, bool autoConnect)
        => status switch
        {
            ChatStatus.Connected => "Waiting for chat…",
            ChatStatus.Disconnected when !autoConnect => "Tap to connect",
            ChatStatus.Disconnected => "Waiting for connection…",
            ChatStatus.Connecting => "Waiting for connection…",
            _ => "Waiting for connection…" // unreachable — guards undefined enum values
        };
}
