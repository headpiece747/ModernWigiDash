namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The poster asset source: the local Steam librarycache path
/// (<c>{steamPath}/appcache/librarycache/{appId}/library_600x900.jpg</c>,
/// the artwork the Steam client downloads with the library metadata) and
/// the CDN fallback shape
/// (<c>https://cdn.akamai.steamstatic.com/steam/apps/{appId}/library_600x900.jpg</c>).
/// The source owns the path/URL facts only; the fetch itself (local read,
/// CDN GET) lives in the service layer behind the injected client seam.
/// </summary>
internal static class SteamPosterSource
{
    /// <summary>The poster file name inside the per-app librarycache folder.</summary>
    public const string PosterFileName = "library_600x900.jpg";

    /// <summary>The CDN poster URL for an app id (the Steam art CDN's
    /// standard per-app path).</summary>
    public static string CdnUrl(uint appId)
        => $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/{PosterFileName}";

    /// <summary>
    /// The local poster path for an app id under a Steam installation, or
    /// null when the file is not present (the CDN fallback leg then runs).
    /// </summary>
    public static string? TryFindLocalPoster(string steamPath, uint appId)
    {
        string path = Path.Combine(steamPath, "appcache", "librarycache", appId.ToString(), PosterFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Reads the local poster bytes, or null when the file is absent or
    /// unreadable (a hostile or half-written file is observable, never a
    /// throw into the caller).
    /// </summary>
    public static byte[]? TryReadLocalPoster(string steamPath, uint appId)
    {
        string? path = TryFindLocalPoster(steamPath, appId);
        if (path is null)
            return null;

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
