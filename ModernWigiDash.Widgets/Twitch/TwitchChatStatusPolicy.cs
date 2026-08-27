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
    /// The NOTICE → connection-state rule: a login-failure notice
    /// (authentication failed, login unsuccessful, improperly formatted
    /// auth, or an invalid nick) disconnects the chat, the "you are not
    /// logged in" notice means the anonymous session is live, and any other
    /// notice leaves the state untouched. <c>Changed</c> reports the actual
    /// transition (a repeated notice that lands on the state the rule
    /// already set is not a transition: the connection logs it as a notice
    /// instead of re-logging the failure and re-publishing the state). The
    /// widget derives its detail text and log channel from the result.
    /// </summary>
    public static (ChatStatus Status, bool Changed) StatusFromNotice(string noticeText, ChatStatus current)
    {
        ArgumentNullException.ThrowIfNull(noticeText);
        ChatStatus status = current;
        if (IsLoginFailureNotice(noticeText))
        {
            status = ChatStatus.Disconnected;
        }
        else if (noticeText.Contains("you are not logged in", StringComparison.OrdinalIgnoreCase))
        {
            status = ChatStatus.Connected;
        }
        return (status, status != current);
    }

    // The login-failure NOTICE texts the server sends (substring match: a
    // real NOTICE carries a server prefix / appended reason, and the match
    // is substring-based by contract).
    private static bool IsLoginFailureNotice(string noticeText)
        => noticeText.Contains("Login authentication failed", StringComparison.OrdinalIgnoreCase)
            || noticeText.Contains("Login unsuccessful", StringComparison.OrdinalIgnoreCase)
            || noticeText.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase)
            || noticeText.Contains("Invalid NICK", StringComparison.OrdinalIgnoreCase);

    /// <summary>The message-buffer trim rule: the chat holds at most the
    /// clamped MaxMessages (5..100) — one spelling shared by the receive path
    /// and the property-change trim, so the bound can never drift between
    /// them.</summary>
    public static int ClampMaxMessages(int value) => Math.Clamp(value, 5, 100);
}
