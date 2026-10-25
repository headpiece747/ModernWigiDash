namespace ModernWigiDash.Widgets.Steam.Models;

/// <summary>
/// A display item for one Steam game in the library widget: the manifest's
/// identity plus the derived display state (the process-tracker fold), the
/// favorite flag (the long-press toggle), and the playtime facts. The
/// service publishes immutable snapshots of these; the render tick reads
/// them and never mutates them.
/// </summary>
internal sealed record SteamGameItem(
    uint AppId,
    string Name,
    string InstallDir,
    SteamGameState State,
    float DownloadProgress,
    double HoursOnRecord,
    DateTime? LastPlayedUtc,
    bool IsFavorite)
{
    /// <summary>Whether the game is in an actionable state (installed and
    /// not mid-update): the launch tap only promises what the state keeps.</summary>
    public bool IsLaunchable => State is SteamGameState.Idle or SteamGameState.Running;
}
