namespace ModernWigiDash.Widgets.Steam.Models;

/// <summary>
/// The Steam Library widget's layout modes (the inspector's "Display Layout"
/// Choice property).
/// </summary>
public enum SteamLayoutMode
{
    /// <summary>Responsive multi-row poster grid with category rows
    /// (Favorites / Installed / Updating), horizontal drag to scroll.</summary>
    PosterGrid,

    /// <summary>Centered focal card with flanking cards, swipe to snap to the
    /// next title.</summary>
    Showcase,
}

/// <summary>
/// The Steam appmanifest (ACF) <c>StateFlags</c> bitmask, the Lutris Steam
/// documentation's table (the reference every Steam tooling uses). The ACF
/// stores the flags as a decimal uint; a missing key reads as
/// <see cref="None"/> (an uninstalled app).
/// </summary>
[Flags]
internal enum SteamAppState
{
    /// <summary>No state bits set.</summary>
    None = 0,

    /// <summary>The app is not installed.</summary>
    Uninstalled = 1,

    /// <summary>An update is required (pending, not active).</summary>
    UpdateRequired = 2,

    /// <summary>The app is fully installed.</summary>
    FullyInstalled = 4,

    /// <summary>The app files are encrypted.</summary>
    Encrypted = 8,

    /// <summary>The app is locked (no changes allowed).</summary>
    Locked = 16,

    /// <summary>Installed files are missing.</summary>
    FilesMissing = 32,

    /// <summary>The app process is running (per the ACF).</summary>
    AppRunning = 64,

    /// <summary>Installed files are corrupt.</summary>
    FilesCorrupt = 128,

    /// <summary>An update is actively running.</summary>
    UpdateRunning = 256,

    /// <summary>An update is paused.</summary>
    UpdatePaused = 512,

    /// <summary>An update was started (queued).</summary>
    UpdateStarted = 1024,

    /// <summary>The app is uninstalling.</summary>
    Uninstalling = 2048,

    /// <summary>A backup is running.</summary>
    BackupRunning = 4096,

    /// <summary>The app is being reconfigured.</summary>
    Reconfiguring = 65536,

    /// <summary>Files are being validated.</summary>
    Validating = 131072,

    /// <summary>Files are being added.</summary>
    AddingFiles = 262144,

    /// <summary>Files are being preallocated.</summary>
    Preallocating = 524288,

    /// <summary>Files are downloading.</summary>
    Downloading = 1048576,

    /// <summary>Files are staging.</summary>
    Staging = 2097152,

    /// <summary>Files are committing.</summary>
    Committing = 4194304,

    /// <summary>An update is stopping.</summary>
    UpdateStopping = 8388608,
}

/// <summary>
/// The active-update subset of <see cref="SteamAppState"/>: any of these
/// bits means the app is mid-update right now (as opposed to
/// <see cref="SteamAppState.UpdateRequired"/>, which is a pending state).
/// </summary>
internal static class SteamAppStateExtensions
{
    private const SteamAppState ActiveUpdate =
        SteamAppState.UpdateRunning
        | SteamAppState.UpdatePaused
        | SteamAppState.UpdateStarted
        | SteamAppState.Downloading
        | SteamAppState.Staging
        | SteamAppState.Committing
        | SteamAppState.AddingFiles;

    /// <summary>Whether the flags carry any active-update bit.</summary>
    public static bool IsActivelyUpdating(this SteamAppState flags)
        => (flags & ActiveUpdate) != SteamAppState.None;
}

/// <summary>
/// The derived display state the widget renders per game: the fold of the
/// ACF flags and the live process check into the four on-screen conditions.
/// </summary>
internal enum SteamGameState
{
    /// <summary>Not installed on any library volume.</summary>
    Uninstalled,

    /// <summary>Installed and idle (launchable).</summary>
    Idle,

    /// <summary>Installed and mid-update (a progress bar renders).</summary>
    Updating,

    /// <summary>The game process is running (the ACTIVE badge renders).</summary>
    Running,
}
