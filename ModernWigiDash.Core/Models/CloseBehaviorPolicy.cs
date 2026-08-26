namespace ModernWigiDash.Core.Models;

/// <summary>
/// The window's close-behavior vocabulary and parse rule (Core/Models): the
/// one owner of what a persisted <see cref="ProfileLayout.CloseBehavior"/>
/// value may name. The profile is a hand-editable traveling artifact, so the
/// value is a raw string (the ThemeSettings precedent) and this policy is the
/// single parse site: a known value wins, and a null (absent, "this profile
/// has no opinion") or unknown (hand-edited) value degrades to the default
/// <see cref="Default"/>. The untrusted-import rule sits beside it in
/// <see cref="ProfileImportSanitizer"/> (absent stays absent, present-but-
/// corrupt normalizes to <see cref="Quit"/>), and the import merge (an
/// imported profile lacking the field keeps the local value) runs at the
/// window's import handler.
/// </summary>
public static class CloseBehaviorPolicy
{
    /// <summary>The "exit the app" close behavior: X, Alt+F4, and minimize
    /// all run the full teardown (the pre-feature behavior).</summary>
    public const string Quit = "quit";

    /// <summary>The "hide to tray" close behavior: the window hides, the
    /// display stays live, and the tray icon's "Quit" is the only exit.</summary>
    public const string HideToTray = "hideToTray";

    /// <summary>The default when the profile has no opinion or carries an
    /// unrecognized value: the pre-feature behavior, a normal exit.</summary>
    public static string Default => Quit;

    /// <summary>True when the value names a known close behavior. The exact
    /// spelling; case is identity, so a hand-edited "QUIT" is unknown, not
    /// quit-with-a-typo.</summary>
    public static bool IsKnown(string? value)
        => value is Quit or HideToTray;

    /// <summary>
    /// Resolves the effective close behavior from a raw persisted value: a
    /// known value wins, everything else (null, whitespace, unknown) degrades
    /// to <see cref="Default"/>. The runtime read routes through here so a
    /// hand-edited or legacy profile can never smuggle in a behavior.
    /// </summary>
    public static string Resolve(string? value)
        => IsKnown(value) ? value! : Default;

    /// <summary>
    /// The import merge: an imported profile lacking the close behavior
    /// (null — "this profile has no opinion") takes the local value, so an
    /// older or hand-crafted profile never drops the local setting; a present
    /// imported value (a known spelling, or the sanitizer's safe-default
    /// normalization of a corrupt one) wins. The local side is resolved
    /// first, so the stamped value is always a known spelling the next export
    /// can carry. The window's import handler is the one caller.
    /// </summary>
    public static string? MergeImport(string? imported, string? local)
        => imported is null ? Resolve(local) : imported;
}
