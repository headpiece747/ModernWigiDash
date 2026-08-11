namespace ModernWigiDash.App;

/// <summary>
/// The shell-open URL trust rule for the App's browser-open sites: only
/// https URLs on twitch.tv may be shell-opened, so a tampered device-auth
/// verification response cannot invoke file:/custom protocol handlers.
/// Mirrors the auto-open guard in TwitchAuthenticationService (Widgets) —
/// the two sites must stay in lockstep.
/// </summary>
internal static class TrustedBrowserUri
{
    public static bool IsTrusted(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps &&
           uri.Host.EndsWith("twitch.tv", StringComparison.OrdinalIgnoreCase);
}
