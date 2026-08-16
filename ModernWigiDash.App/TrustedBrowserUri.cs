using ModernWigiDash.Sdk;

namespace ModernWigiDash.App;

/// <summary>
/// The shell-open URL trust rule for the App's browser-open sites: only
/// https URLs on twitch.tv may be shell-opened, so a tampered device-auth
/// verification response cannot invoke file:/custom protocol handlers.
/// Delegates the host rule to the shared <see cref="TrustedUriPolicy"/> — the
/// same policy the Widgets auto-open guard binds.
/// </summary>
internal static class TrustedBrowserUri
{
    public static bool IsTrusted(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
           TrustedUriPolicy.IsTwitchAuthorizationHost(uri.Host);
}
