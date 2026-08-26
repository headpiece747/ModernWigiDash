namespace ModernWigiDash.Widgets;

/// <summary>
/// One hotkey action-type entry: the persisted/display name, the
/// <see cref="HotkeyActionKind"/> it produces, and the fixed value
/// (the media key for media actions, the page delta for the page-navigate
/// actions; null means the action reads the user's Action Path/Command
/// instead).
/// </summary>
internal sealed record HotkeyActionEntry(string Name, HotkeyActionKind Kind, string? FixedValue);

/// <summary>
/// The single owner of the hotkey action-type vocabulary: the name set the
/// inspector presents, the name-to-(kind, fixed value) mapping, and the
/// "needs a command value" rule. <see cref="Create"/> and
/// <see cref="NeedsCommand"/> read <see cref="Entries"/> here, and the
/// widget's [WidgetProperty] choice array (a compile-time attribute literal,
/// which cannot bind a runtime value) is pinned to it in lockstep by test,
/// so a renamed or hand-edited choice fails the pin instead of sailing to
/// the Launch default.
/// </summary>
internal static class HotkeyActionCatalog
{
    /// <summary>The default action type: the attribute default and the
    /// property default both spell it from this const.</summary>
    public const string DefaultName = "Launch App";

    public static readonly IReadOnlyList<HotkeyActionEntry> Entries =
    [
        new(DefaultName, HotkeyActionKind.Launch, null),
        new("Open URL", HotkeyActionKind.OpenUrl, null),
        new("Media Play / Pause", HotkeyActionKind.MediaKey, MediaKeyCatalog.PlayPause),
        new("Media Next", HotkeyActionKind.MediaKey, MediaKeyCatalog.Next),
        new("Media Previous", HotkeyActionKind.MediaKey, MediaKeyCatalog.Previous),
        new("Media Stop", HotkeyActionKind.MediaKey, MediaKeyCatalog.Stop),
        new("Volume Up", HotkeyActionKind.MediaKey, MediaKeyCatalog.VolumeUp),
        new("Volume Down", HotkeyActionKind.MediaKey, MediaKeyCatalog.VolumeDown),
        new("Mute", HotkeyActionKind.MediaKey, MediaKeyCatalog.Mute),
        new("Next Page", HotkeyActionKind.PageNavigate, "1"),
        new("Previous Page", HotkeyActionKind.PageNavigate, "-1"),
        new("Run AHK Script", HotkeyActionKind.AhkScript, null),
    ];

    /// <summary>
    /// True when the named action reads a command value (Launch/URL/AHK script): the
    /// inspector's action-command editor visibility and the executor's
    /// empty-command skip both route through this. An unknown name (a
    /// hand-edited profile) is false: it degrades to the Launch kind, and an
    /// empty command then fails the executor's path validation (the pinned
    /// unknown-name rule), never a silent no-op.
    /// </summary>
    public static bool NeedsCommand(string actionType)
        => Find(actionType) is { FixedValue: null };

    /// <summary>
    /// Maps the action-type name to its action. A name absent from the
    /// catalog (a hand-edited profile) degrades to the Launch kind with the
    /// raw command: the former switch's default branch, now the one
    /// unknown-name rule.
    /// </summary>
    public static HotkeyAction Create(string actionType, string actionCommand)
    {
        HotkeyActionEntry? entry = Find(actionType);
        return entry is null
            ? new HotkeyAction { Kind = HotkeyActionKind.Launch, Value = actionCommand }
            : new HotkeyAction { Kind = entry.Kind, Value = entry.FixedValue ?? actionCommand };
    }

    private static HotkeyActionEntry? Find(string actionType)
        => Entries.FirstOrDefault(e => string.Equals(e.Name, actionType, StringComparison.Ordinal));
}
