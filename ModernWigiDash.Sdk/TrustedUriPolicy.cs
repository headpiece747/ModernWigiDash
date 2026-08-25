namespace ModernWigiDash.Sdk;

/// <summary>
/// The shell-open trust rule for the app's Twitch authorization sites: only
/// https URLs whose host IS the twitch.tv apex or a dot-prefixed subdomain may
/// be shell-opened, so a tampered device-auth verification response cannot
/// invoke file:/custom protocol handlers. An EndsWith-style suffix match would
/// also accept attacker-registrable lookalikes (faketwitch.tv). The composite
/// <see cref="IsTwitchAuthorizationUri"/> (https scheme AND trusted host) is
/// the one gate every shell-open site routes through.
/// </summary>
public static class TrustedUriPolicy
{
    /// <summary>True when <paramref name="host"/> is twitch.tv or a subdomain of it.</summary>
    public static bool IsTwitchAuthorizationHost(string? host)
        => string.Equals(host, "twitch.tv", StringComparison.OrdinalIgnoreCase)
        || (host?.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>True when the URI may be shell-opened: an https URL on a trusted host.</summary>
    public static bool IsTwitchAuthorizationUri(Uri uri)
        => string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
        && IsTwitchAuthorizationHost(uri.Host);
}
