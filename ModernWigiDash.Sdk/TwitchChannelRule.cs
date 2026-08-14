namespace ModernWigiDash.Sdk;

/// <summary>
/// The Twitch channel-name rule shared by the profile sanitizer (Core) and the
/// chat widget (Widgets): a channel may not carry embedded CR/LF (an IRC line
/// injection into the JOIN command) and may not exceed Twitch's 25-char cap.
/// <see cref="Sanitize"/> returns the caller's fallback for invalid names —
/// the sanitizer clears to empty, the widget falls back to "twitch".
/// </summary>
public static class TwitchChannelRule
{
    /// <summary>Twitch's channel-name length cap.</summary>
    public const int MaxChannelNameLength = 25;

    /// <summary>True when <paramref name="channel"/> is safe to join: within
    /// the length cap and free of CR/LF.</summary>
    public static bool IsValid(string channel)
        => channel.Length <= MaxChannelNameLength && channel.IndexOfAny(['\r', '\n']) < 0;

    /// <summary>Returns <paramref name="channel"/> when valid, else
    /// <paramref name="fallback"/> (each caller supplies its own: the profile
    /// sanitizer clears invalid imported values to empty; the widget falls back
    /// to its empty-channel placeholder).</summary>
    public static string Sanitize(string? channel, string fallback)
    {
        if (string.IsNullOrEmpty(channel)) return fallback;
        return IsValid(channel) ? channel : fallback;
    }
}
