namespace ModernWigiDash.Widgets.Twitch;

/// <summary>
/// The Twitch chat connection-state policy: the NOTICE → connection-state
/// transition and the message-buffer bound, split out of the presentation
/// module so the display rules and the state policy are independently
/// assertable.
/// </summary>
public static class TwitchChatStatusPolicy
{
    /// <summary>
    /// The NOTICE → connection-state rule: a login-failure notice (or an
    /// invalid nick) disconnects the chat, the "you are not logged in" notice
    /// means the anonymous session is live, and any other notice leaves the
    /// state untouched. The widget derives its detail text and log channel
    /// from the result.
    /// </summary>
    public static (ChatStatus Status, bool Changed) StatusFromNotice(string noticeText, ChatStatus current)
    {
        ArgumentNullException.ThrowIfNull(noticeText);
        if (noticeText.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase)
            || noticeText.Contains("Invalid NICK", StringComparison.OrdinalIgnoreCase))
        {
            return (ChatStatus.Disconnected, true);
        }

        if (noticeText.Contains("you are not logged in", StringComparison.OrdinalIgnoreCase))
        {
            return (ChatStatus.Connected, true);
        }

        return (current, false);
    }

    /// <summary>The message-buffer trim rule: the chat holds at most the
    /// clamped MaxMessages (5..100) — one spelling shared by the receive path
    /// and the property-change trim, so the bound can never drift between
    /// them.</summary>
    public static int ClampMaxMessages(int value) => Math.Clamp(value, 5, 100);
}
