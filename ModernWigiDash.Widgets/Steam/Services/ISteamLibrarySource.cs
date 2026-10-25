using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Parsing;

namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The Steam library source seam: the installation path and the manifest
/// enumeration. The production implementation wraps
/// <see cref="SteamLibraryLocator"/>; tests inject a fake so the service's
/// refresh and poster-warmup logic run against a fixed library without a
/// real Steam installation.
/// </summary>
internal interface ISteamLibrarySource
{
    /// <summary>The Steam installation path, or null when none is found.</summary>
    string? SteamPath { get; }

    /// <summary>
    /// The library manifests for every mounted volume (empty when Steam is
    /// not installed or no manifests are readable).
    /// </summary>
    IReadOnlyList<SteamAppManifest> LoadManifests();
}

/// <summary>
/// The production <see cref="ISteamLibrarySource"/> over
/// <see cref="SteamLibraryLocator"/>. The path is re-resolved on every
/// <see cref="SteamPath"/> read (a cheap registry ladder) so a Steam
/// installation that appears after startup is picked up on the next tick.
/// </summary>
internal sealed class SteamLibrarySource : ISteamLibrarySource
{
    private readonly Action<string>? _log;

    /// <summary>The production source over the locator (log lines go to the widget host).</summary>
    public SteamLibrarySource(Action<string>? log = null)
    {
        _log = log;
    }

    public string? SteamPath => SteamLibraryLocator.TryFindSteamPath();

    public IReadOnlyList<SteamAppManifest> LoadManifests()
    {
        string? path = SteamPath;
        if (path is null)
            return [];

        IReadOnlyList<string> roots = SteamLibraryLocator.GetLibraryRoots(path);
        return SteamLibraryLocator.LoadManifests(roots, _log);
    }
}
