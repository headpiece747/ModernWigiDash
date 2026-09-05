namespace ModernWigiDash.App;

/// <summary>
/// The settings hub's named host seam (App, the ADR-0008 image): the one
/// contract between the <see cref="Dialogs.SettingsDialog"/> and the window
/// that hosts it. The hub reads its six open-time seeds once from
/// <see cref="Seed"/> (the host's persisted state at open time) and routes
/// every write-through and file flow through the commit members, so the
/// dialog crosses one typed seam instead of a 14-argument positional delegate
/// bag, and the tests bind an in-memory fake host. The window is the
/// production host; each commit member is the window's existing write-through
/// seam (the control write is the change, there is no Apply step).
/// </summary>
internal interface ISettingsHubHost
{
    /// <summary>The hub's open-time seeds, read live by the host.</summary>
    SettingsHubSeed Seed { get; }

    /// <summary>The close-behavior write-through (the radio's check is the
    /// change; the host commits the profile and marks it dirty).</summary>
    void CommitCloseBehavior(string value);

    /// <summary>The Start-with-Windows write-through (the host writes or
    /// deletes the HKCU Run entry; the registry is the single source of
    /// truth).</summary>
    void CommitAutostart(bool enabled);

    /// <summary>The kill-switch write-through (the host persists the
    /// machine-local setting and re-runs the idempotent hotkey registration
    /// pass).</summary>
    void CommitKillSwitch(bool tripped);

    /// <summary>The AutoHotkey interpreter write-through (the host persists
    /// the machine-local path).</summary>
    void CommitAhkInterpreter(string path);

    /// <summary>The interpreter Browse (the host owns the file dialog);
    /// returns the chosen path so the hub's box cannot drift from the
    /// persisted one, or null on cancel.</summary>
    string? BrowseAhkInterpreter();

    /// <summary>The Profile group's export (the host's file flow).</summary>
    void ExportProfile();

    /// <summary>The Profile group's import (the host's file flow).</summary>
    void ImportProfile();

    /// <summary>The page-background write-through (the host writes the
    /// active page's background and marks it dirty).</summary>
    void CommitPageBackground(string hex);

    /// <summary>The minimize-to-tray-on-startup write-through (the host
    /// persists the machine-local setting; the next launch opens hidden).</summary>
    void CommitMinimizeToTrayOnStartup(bool enabled);
}
