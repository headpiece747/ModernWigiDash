using ModernWigiDash.Widgets.Steam.Models;

namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// An immutable snapshot of the Steam library the service publishes: the
/// resolved game items (state folded, favorites applied), the Steam
/// installation path, and whether a Steam installation was found at all. The
/// widget reads the volatile published reference on the render tick and never
/// mutates it — the service swaps in a fresh snapshot each 3-second tick.
/// </summary>
internal sealed record SteamLibrarySnapshot(
    IReadOnlyList<SteamGameItem> Games,
    string? SteamPath,
    bool IsSteamFound,
    SteamPersona? Persona)
{
    /// <summary>The empty snapshot (no Steam installation, no games, no persona).</summary>
    public static SteamLibrarySnapshot Empty { get; } = new([], null, false, null);
}
