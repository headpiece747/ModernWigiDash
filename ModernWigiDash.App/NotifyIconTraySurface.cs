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
    private readonly bool _ownsIcon;
    private bool _live;

    private NotifyIconTraySurface(Icon? icon, TrayMenu menu)
    {
        // A null icon is legal here: Show() then refuses to bring the icon
        // up and IsLive stays false, so the close path's N1 guard falls the
        // close through to a normal exit instead of hiding into a void.
        // Track whether this surface owns the icon handle (SystemIcons.Application
        // is a shared static instance that must not be disposed).
        _ownsIcon = icon is not null && !ReferenceEquals(icon, System.Drawing.SystemIcons.Application);
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

    internal static Icon? LoadIcon()
    {
        // 1. Loose file next to executable (standard directory layout)
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "Logo", "logo.ico");
            if (File.Exists(path))
            {
                return new Icon(path);
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Fall through to embedded / exe resource
        }

        // 2. WPF pack URI embedded resource
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/Logo/logo.ico", UriKind.Absolute);
            var streamInfo = System.Windows.Application.GetResourceStream(uri)
                ?? System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/ModernWigiDash.App;component/Resources/Logo/logo.ico", UriKind.Absolute));
            if (streamInfo?.Stream is { } stream)
            {
                using (stream)
                {
                    return new Icon(stream);
                }
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Fall through to associated executable icon
        }

        // 3. Current executable associated icon (ApplicationIcon in PE header)
        try
        {
            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Fall through to system default
        }

        // 4. System default application icon (never null)
        try
        {
            return System.Drawing.SystemIcons.Application;
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
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
        // The NotifyIcon removes the icon from the notification area;
        // explicitly dispose the created Icon handle if owned.
        _live = false;
        _notifyIcon.Visible = false;
        if (_ownsIcon)
        {
            var icon = _notifyIcon.Icon;
            _notifyIcon.Icon = null;
            icon?.Dispose();
        }
        _notifyIcon.Dispose();
        _menuStrip.Dispose();
    }
}
