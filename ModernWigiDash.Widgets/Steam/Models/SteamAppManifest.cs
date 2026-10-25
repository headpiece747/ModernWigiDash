namespace ModernWigiDash.Widgets.Steam.Models;

/// <summary>
/// An immutable record of one Steam appmanifest (ACF) file: the app's id,
/// name, install directory, launch executable, state flags, download
/// progress counters, last played stamp, and total playtime. Built once per
/// background refresh by <c>SteamLibraryLocator</c>; the render tick only
/// reads the derived properties, never re-parses.
/// </summary>
internal sealed record SteamAppManifest(
    uint AppId,
    string Name,
    string InstallDir,
    string Executable,
    SteamAppState StateFlags,
    long BytesDownloaded,
    long BytesToDownload,
    long LastPlayed,
    double HoursOnRecord)
{
    /// <summary>
    /// The process-matching key: the launch executable's name without its
    /// <c>.exe</c> extension (the shape <c>Process.GetProcessesByName</c>
    /// takes). Empty when the ACF carries no executable fact (the process
    /// check then reads as not running).
    /// </summary>
    public string ProcessName
    {
        get
        {
            if (Executable.Length == 0)
                return "";

            const string suffix = ".exe";
            return Executable.EndsWith(suffix, System.StringComparison.OrdinalIgnoreCase)
                ? Executable[..^suffix.Length]
                : Executable;
        }
    }

    /// <summary>Whether the app is fully installed on a library volume.</summary>
    public bool IsInstalled => StateFlags.HasFlag(SteamAppState.FullyInstalled);

    /// <summary>Whether the app is mid-update (an active-update flag is set).</summary>
    public bool IsUpdating => StateFlags.IsActivelyUpdating();

    /// <summary>
    /// The download progress as 0..1: 1 for a fully installed app with
    /// nothing pending, the downloaded fraction while bytes remain, 0 for an
    /// app with no progress facts.
    /// </summary>
    public float DownloadProgress
    {
        get
        {
            if (BytesToDownload <= 0)
                return IsInstalled ? 1f : 0f;

            long total = BytesDownloaded + BytesToDownload;
            if (total <= 0)
                return 0f;

            float fraction = (float)(BytesDownloaded / (double)total);
            return Math.Clamp(fraction, 0f, 1f);
        }
    }

    /// <summary>
    /// The last-played stamp as a UTC DateTime (null when the ACF carries no
    /// positive Unix timestamp).
    /// </summary>
    public DateTime? LastPlayedUtc
    {
        get
        {
            if (LastPlayed <= 0)
                return null;

            return DateTimeOffset.FromUnixTimeSeconds(LastPlayed).UtcDateTime;
        }
    }
}
