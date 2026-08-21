namespace ModernWigiDash.Widgets.Twitch;

/// <summary>
/// Pure display rules for the Twitch chat widget: the header status line, the
/// empty-state hint, and the (status, detail) status payload the connection
/// machine stores — its named transition factories are the ONE spelling of
/// every status detail the widget produces, so no state-change site spells a
/// detail string of its own. The plain-disconnected state is the only payload
/// with an empty detail; the header spells it as "Disconnected". The
/// connection-state policy lives in <see cref="TwitchChatStatusPolicy"/>.
/// </summary>
public static class TwitchChatPresentation
{
    /// <summary>One connection-state payload: the status plus the detail the
    /// header line spells after the state dot. The named factories are the
    /// widget's state-change vocabulary — each transition site stores one of
    /// these instead of composing the pair itself.</summary>
    public sealed record ChatState(ChatStatus Status, string Detail)
    {
        /// <summary>The idle state: no connection attempt in flight.</summary>
        public static ChatState Disconnected() => new(ChatStatus.Disconnected, "");

        /// <summary>The socket is coming up.</summary>
        public static ChatState Connecting() => new(ChatStatus.Connecting, "Connecting…");

        /// <summary>The socket is open and the JOIN handshake is in flight.</summary>
        public static ChatState JoiningChannel(string channel) => new(ChatStatus.Connecting, "Joining #" + channel + "…");

        /// <summary>The connection dropped and the reconnect backoff is running.</summary>
        public static ChatState Reconnecting() => new(ChatStatus.Disconnected, "Reconnecting…");

        /// <summary>The chat is live.</summary>
        public static ChatState Live() => new(ChatStatus.Connected, "LIVE");

        /// <summary>A login-failure NOTICE disconnected the chat.</summary>
        public static ChatState LoginFailed() => new(ChatStatus.Disconnected, "Login failed — check token & username");
    }

    /// <summary>The header status line: a state dot plus the payload's detail.
    /// The Connected and Connecting payloads always carry their detail (their
    /// factories spell it); the plain-disconnected payload is the only one with
    /// an empty detail, spelled here.</summary>
    public static string StatusText(ChatState state)
        => state.Status switch
        {
            ChatStatus.Connected => "● " + state.Detail,
            ChatStatus.Connecting => "⟳ " + state.Detail,
            _ => "○ " + (state.Detail.Length > 0 ? state.Detail : "Disconnected") // also guards undefined enum values
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

    /// <summary>The header status color: green when the chat is live, white otherwise.</summary>
    public static SKColor StatusColor(ChatStatus status)
        => status == ChatStatus.Connected ? new SKColor(0x10, 0xB9, 0x81) : SKColors.White;
}
