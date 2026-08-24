using System.Text.Json;

namespace ModernWigiDash.App.Update;

/// <summary>
/// Pure update-decision logic: parse the GitHub releases/latest JSON, compare
/// SemVer, and pick the slim app-only asset. No I/O — testable via a JSON string.
/// </summary>
internal static class UpdateChecker
{
    /// <summary>Returns the pending slim update when the latest release is newer
    /// than <paramref name="currentVersion"/>, else null. Null current (dev) never updates.</summary>
    public static UpdateInfo? ParseLatestRelease(string json, Version? currentVersion)
    {
        if (currentVersion is null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            var latest = AppVersion.Parse(tagEl.GetString());
            if (latest is null || latest <= currentVersion) return null;

            string? url = PickAppOnlyAsset(root);
            string? digest = FindDigest(root, url);
            if (url is null || digest is null) return null;

            return new UpdateInfo(
                $"{latest.Major}.{latest.Minor}.{latest.Build}",
                url,
                digest);
        }
        // Wrong-typed fields (tag_name as a number, assets as an object, a
        // numeric browser_download_url) throw InvalidOperationException from
        // the JsonElement accessors; both shapes mean "not a valid release
        // payload" -> null, never an escape past the invalid->null contract.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Picks the app-only slim asset's download URL, or null when absent.</summary>
    internal static string? PickAppOnlyAsset(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (name.StartsWith("ModernWigiDash-v", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith("-app-only.zip", StringComparison.OrdinalIgnoreCase)
                && asset.TryGetProperty("browser_download_url", out var u))
            {
                // The host trust rule for asset downloads: a release payload
                // served from any host but GitHub's own is the MITM shape
                // that would otherwise ship a self-consistent digest for a
                // swapped zip (the digest and the bytes come from the same
                // payload, so only the host is independent evidence).
                string? url = u.GetString();
                return IsTrustedAssetUrl(url) ? url : null;
            }
        }
        return null;
    }

    /// <summary>
    /// The host trust rule for release asset downloads: only https URLs on
    /// GitHub's own hosts (release asset downloads live on github.com and
    /// redirect to objects.githubusercontent.com). Anything else is refused.
    /// </summary>
    internal static bool IsTrustedAssetUrl(string? url)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)) return false;
        return uri.Host is "github.com" or "objects.githubusercontent.com";
    }

    private static string? FindDigest(JsonElement release, string? url)
    {
        if (url is null || !release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            string u = asset.TryGetProperty("browser_download_url", out var ue) ? ue.GetString() ?? "" : "";
            if (string.Equals(u, url, StringComparison.Ordinal) && asset.TryGetProperty("digest", out var d))
            {
                // GitHub's digest is "sha256:<hex>" — normalize to the raw hex
                // the SHA-256 comparison uses.
                string? raw = d.GetString();
                if (raw is null) return null;
                // The algorithm must be named by the payload: a digest whose
                // algorithm we cannot trust is not a digest we can compare
                // (GitHub always sends "sha256:<hex>"; a prefix-less or
                // foreign-algorithm digest means the payload is not a trusted
                // release digest, and the release is invalid).
                const string prefix = "sha256:";
                return raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? raw[prefix.Length..]
                    : null;
            }
        }
        return null;
    }
}
