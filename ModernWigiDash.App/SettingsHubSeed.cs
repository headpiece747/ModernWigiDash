namespace ModernWigiDash.App;

/// <summary>
/// The settings hub's open-time seeds (App, ADR-0018/0019): the six
/// persisted values the hub reads once when it opens (the raw close-behavior
/// value, the autostart entry's presence, the kill-switch state, the
/// AutoHotkey interpreter path, the active page's background, and the
/// minimize-to-tray-on-startup flag). The window builds the record live at
/// open time through <see cref="ISettingsHubHost.Seed"/>, so a new seeded row
/// is a named addition to this record, not a positional argument on the
/// dialog's constructor.
/// </summary>
internal sealed record SettingsHubSeed(
    string? CloseBehavior,
    bool Autostart,
    bool KillSwitch,
    string AhkInterpreterPath,
    string PageBackground,
    bool MinimizeToTrayOnStartup);
