namespace ModernWigiDash.App;

/// <summary>
/// The notification-area icon's policy module (App, ADR-0018): owns the
/// icon's show/hide lifecycle, routes the surface's click and menu events
/// to the injected show/quit delegates (the ONE routing spelling — the
/// production adapter is a thin WinForms binding, the tests bind an
/// in-memory fake), and exposes <see cref="IsLive"/> — the N1 guard the
/// close path reads (a tray that never came up falls the close through to a
/// normal exit instead of hiding into a void).
/// </summary>
internal sealed class TrayIconController(
    Action onShow,
    Action onQuit,
    DiagLog? log = null,
    ITrayIconSurface? surface = null) : IDisposable
{
    private readonly DiagLog _log = log ?? new DiagLog("TRAY", 1);
    private ITrayIconSurface? _surface;

    /// <summary>True while the icon is actually alive in the notification
    /// area (the N1 guard: a dead tray falls the close through to a normal
    /// exit). False before <see cref="Start"/> and after <see cref="Dispose"/>
    /// — a close path reading this never sees a stale "live" after teardown
    /// removed the icon.</summary>
    public bool IsLive => _surface?.IsLive ?? false;

    /// <summary>Creates the surface (the production <c>NotifyIcon</c> when
    /// none is injected), wires its click/menu events to the show/quit
    /// delegates, and shows the icon. Idempotent: a second Start is a no-op
    /// (the icon is already up, the events already wired — re-wiring would
    /// double-fire the show on one click).</summary>
    public void Start()
    {
        if (_surface is not null)
        {
            return;
        }

        _surface = surface ?? NotifyIconTraySurface.Create(TrayMenu.Default());
        _surface.SingleClicked += () => onShow();
        _surface.MenuSelected += OnMenuSelected;
        _surface.Show();
        _log.Write("icon shown");
    }

    /// <summary>Removes the icon and releases the surface (idempotent,
    /// safe to call without a <see cref="Start"/>). The teardown plan's
    /// TrayDispose step routes through here, so the icon is gone before the
    /// process exits and a force-killed test host never leaves a ghost
    /// icon.</summary>
    public void Dispose()
    {
        if (_surface is null)
        {
            return;
        }

        _surface.Hide();
        _surface.Dispose();
        _surface = null;
        _log.Write("icon removed");
    }

    /// <summary>The one routing spelling for a menu selection: Show and
    /// Quit forward to the injected delegates; the separator is unselectable
    /// (the adapter renders it as a separator and never raises it — the arm
    /// exists so an added command can never silently no-op).</summary>
    private void OnMenuSelected(TrayMenuCommand command)
    {
        switch (command)
        {
            case TrayMenuCommand.Show:
                onShow();
                break;
            case TrayMenuCommand.Quit:
                onQuit();
                break;
            case TrayMenuCommand.Separator:
                // Unreachable: a separator is a layout entry, never a
                // selectable item.
                break;
        }
    }
}
