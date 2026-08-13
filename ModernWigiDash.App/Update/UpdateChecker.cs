using System.Text.Json;

namespace ModernWigiDash.App.Update;

/// <summary>
/// Pure update-decision logic: parse the GitHub releases/latest JSON, compare
/// SemVer, and pick the slim app-only asset. No I/O — testable via a JSON string.
/// </summary>
public static class UpdateChecker
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
                return u.GetString();
            }
        }
        return null;
    }

    private static string? FindDigest(JsonElement release, string? url)
    {
        if (url is null || !release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            string u = asset.TryGetProperty("browser_download_url", out var ue) ? ue.GetString() ?? "" : "";
            if (u == url && asset.TryGetProperty("digest", out var d))
            {
                // GitHub's digest is "sha256:<hex>" — normalize to the raw hex
                // the SHA-256 comparison uses.
                string? raw = d.GetString();
                if (raw is null) return null;
                const string prefix = "sha256:";
                return raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? raw[prefix.Length..]
                    : raw;
            }
        }
        return null;
    }
}
