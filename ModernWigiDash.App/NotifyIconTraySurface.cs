using System.IO;
using System.Windows.Forms;

// Icon lives in System.Drawing (no whole-namespace using: System.Drawing.Path
// would collide with the global System.IO.Path).
using Icon = System.Drawing.Icon;

namespace ModernWigiDash.App;

/// <summary>
/// The production tray surface (App): the WinForms <c>NotifyIcon</c> binding
/// behind <see cref="ITrayIconSurface"/>. Owns the icon handle (the
/// <c>NotifyIcon</c> disposes it on <c>Dispose</c>, through its DestroyIcon
/// path), renders the <see cref="TrayMenu"/> as a context menu in entry
/// order, and raises the seam's events: <see cref="SingleClicked"/> on a
/// single left click, <see cref="MenuSelected"/> on a menu selection. The
/// right-click menu show is the <c>NotifyIcon</c>'s built-in behavior for an
/// assigned <c>ContextMenuStrip</c>; the surface adds no menu logic of its
/// own (the command routing is the controller's).
/// </summary>
internal sealed class NotifyIconTraySurface : ITrayIconSurface
{
    private readonly NotifyIcon _notifyIcon = new();
    private readonly ContextMenuStrip _menuStrip = new();
    private bool _live;

    private NotifyIconTraySurface(Icon? icon, TrayMenu menu)
    {
        // A null icon is legal here: Show() then refuses to bring the icon
        // up and IsLive stays false, so the close path's N1 guard falls the
        // close through to a normal exit instead of hiding into a void.
        _notifyIcon.Icon = icon;
        _notifyIcon.Text = "ModernWigiDash";
        _notifyIcon.Visible = false;

        foreach (TrayMenuItem item in menu.Items)
        {
            if (item.Command == TrayMenuCommand.Separator)
            {
                _menuStrip.Items.Add(new ToolStripSeparator());
                continue;
            }

            var entry = new ToolStripMenuItem(item.Label);
            // The entry captures its own command: the selection raises the
            // seam event and the controller decides what it means.
            entry.Click += (_, _) => MenuSelected?.Invoke(item.Command);
            _menuStrip.Items.Add(entry);
        }

        _notifyIcon.ContextMenuStrip = _menuStrip;
        _notifyIcon.MouseClick += OnMouseClick;
    }

    /// <summary>Creates the production surface with the app icon (the
    /// csproj's <c>Resources/Logo/logo.ico</c> output copy, next to the exe)
    /// and the given menu. The icon load is best-effort: a missing or
    /// corrupt file degrades to a no-icon tray (the N1 guard makes the close
    /// fall through to a normal exit) instead of a startup throw.</summary>
    public static NotifyIconTraySurface Create(TrayMenu menu) => new(LoadIcon(), menu);

    private static Icon? LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "Logo", "logo.ico");
        try
        {
            return File.Exists(path) ? new Icon(path) : null;
        }
        catch (Exception)
        {
            // Best-effort icon: a broken ico must not kill the tray.
            return null;
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        // Left click only: the right button drives the context menu
        // (the built-in ContextMenuStrip show) and must not re-show.
        if (e.Button == MouseButtons.Left)
        {
            SingleClicked?.Invoke();
        }
    }

    /// <summary>Whether this surface brought the icon up and has not taken
    /// it down (the N1 guard, read by the close path). The .NET WinForms
    /// <c>NotifyIcon</c> exposes no shell-side visibility query, so this is
    /// the surface's own live state: true after a real Show, false after
    /// Hide or Dispose, and false when the icon could never load.</summary>
    public bool IsLive => _live;

    public void Show()
    {
        if (_notifyIcon.Icon is null)
        {
            // No icon (the ico was missing or unreadable): never bring the
            // tray up. IsLive stays false and the N1 guard takes over.
            return;
        }

        _notifyIcon.Visible = true;
        _live = true;
    }

    public void Hide()
    {
        _notifyIcon.Visible = false;
        _live = false;
    }

    public event Action? SingleClicked;
    public event Action<TrayMenuCommand>? MenuSelected;

    public void Dispose()
    {
        // The NotifyIcon owns the icon handle (its DestroyIcon releases the
        // HICON); the menu strip owns its own GDI surface.
        _live = false;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menuStrip.Dispose();
    }
}
