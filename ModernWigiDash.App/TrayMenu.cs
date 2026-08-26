namespace ModernWigiDash.App;

/// <summary>
/// The tray menu's command vocabulary (App): what a context-menu selection
/// asks the window to do. <see cref="Separator"/> is a layout entry, not a
/// command a user selects (the production adapter renders it as a separator
/// and never raises it).
/// </summary>
internal enum TrayMenuCommand
{
    /// <summary>Show and activate the main window.</summary>
    Show,

    /// <summary>Exit the app (the normal close sequence, then shutdown).</summary>
    Quit,

    /// <summary>The layout separator between the show item and Quit.</summary>
    Separator,
}

/// <summary>
/// One tray menu entry: the label as drawn and the command a selection
/// fires. The menu is data the controller routes (the WinForms adapter
/// renders it), so the approved tray shape is assertable without a
/// notification area.
/// </summary>
internal sealed record TrayMenuItem(string Label, TrayMenuCommand Command);

/// <summary>
/// The tray menu as data (App): the ONE spelling of the icon's context menu
/// — the app-name show item, the separator, and Quit (the approved tray
/// contract). The production adapter renders the entries in order; a menu
/// whose shape drifts from this record fails the pin instead of sailing to
/// the tray.
/// </summary>
internal sealed record TrayMenu(IReadOnlyList<TrayMenuItem> Items)
{
    /// <summary>The production menu: ModernWigiDash (show) / separator /
    /// Quit.</summary>
    public static TrayMenu Default() => new(
    [
        new TrayMenuItem("ModernWigiDash", TrayMenuCommand.Show),
        new TrayMenuItem(string.Empty, TrayMenuCommand.Separator),
        new TrayMenuItem("Quit", TrayMenuCommand.Quit),
    ]);
}
