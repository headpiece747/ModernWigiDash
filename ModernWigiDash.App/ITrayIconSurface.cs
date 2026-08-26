namespace ModernWigiDash.App;

/// <summary>
/// The tray icon's WinForms-specific surface behind the controller's seam
/// (App): production binds <see cref="NotifyIconTraySurface"/> (the real
/// <c>NotifyIcon</c>), the tests bind an in-memory fake, so the click/menu
/// routing and the IsLive guard are drivable without a notification area.
/// </summary>
internal interface ITrayIconSurface : IDisposable
{
    /// <summary>True while the surface is holding the icon up (shown and not
    /// hidden; a surface that could never bring the icon up reports false).
    /// The N1 guard: a dead tray falls the close through to a normal exit
    /// instead of hiding into a void.</summary>
    bool IsLive { get; }

    /// <summary>Makes the icon visible in the notification area.</summary>
    void Show();

    /// <summary>Removes the icon from the notification area.</summary>
    void Hide();

    /// <summary>Raised on a single left click (the show affordance).</summary>
    event Action? SingleClicked;

    /// <summary>Raised on a context-menu selection: the command the user
    /// picked. The separator never raises (it is a layout entry).</summary>
    event Action<TrayMenuCommand>? MenuSelected;
}
