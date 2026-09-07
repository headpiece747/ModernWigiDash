namespace ModernWigiDash.Widgets;

/// <summary>
/// The shell-open trust rule (Widgets): the one owner of which URL schemes may
/// be handed to the OS default handler. Only http, https, and mailto are
/// allowed -- a file:, ftp:, or other scheme is refused before the spawn, so a
/// hand-edited profile or a hostile event link cannot smuggle a local path into
/// Process.Start. Every shell-open site routes through this instead of
/// re-deriving the scheme allowlist: the hotkey executor's OpenUrl action and
/// the calendar widget's meeting-link open both gate here, so the two can never
/// drift. (The former home was HotkeyActionPolicy.IsAllowedUrl; the rule is a
/// general shell-open concern, not a hotkey-executor detail.)
/// </summary>
internal static class ShellOpenPolicy
{
    /// <summary>Whether the URL names an allowed shell-open scheme (http,
    /// https, or mailto). A blank or non-absolute value is refused.</summary>
    public static bool IsAllowedUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
           && uri.Scheme is "http" or "https" or "mailto";
}
