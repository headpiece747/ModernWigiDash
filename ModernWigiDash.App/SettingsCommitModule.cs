namespace ModernWigiDash.App;

/// <summary>
/// The settings hub's commit module (App, ADR-0018/0019): the one owner of the
/// five machine-local and profile write-throughs the hub routes through its
/// named host seam (<see cref="ISettingsHubHost"/>). Each commit is the same
/// shape - read the store, mutate, persist, log, and re-run the idempotent
/// hotkey registration pass when the setting feeds it - so a new seeded row is
/// one method here instead of a window edit plus a new host member. The window
/// keeps only the dialog open and the seed read; this module holds the policy.
/// </summary>
internal sealed class SettingsCommitModule(
    Func<ProfileLayout> profileProvider,
    Action markDirty,
    Func<AppSettings> appSettingsProvider,
    Action<AppSettings> saveAppSettings,
    IAutostartStore autostartStore,
    Action refreshGlobalHotkeys,
    DiagLog startupLog,
    DiagLog hotkeyLog)
{
    /// <summary>The close-behavior write-through (ADR-0018): the radio's check
    /// is the change, so the profile is committed and marked dirty in place -
    /// no Apply step to forget. The canvas is untouched (the setting is not a
    /// page/widget mutation), so no mutation-shape refresh runs.</summary>
    public void CommitCloseBehavior(string value)
    {
        profileProvider().CloseBehavior = value;
        markDirty();
    }

    /// <summary>The Start-with-Windows write-through (ADR-0019): the checkbox's
    /// check is the change, so the Run entry is written or deleted in place -
    /// no Apply step to forget, and the profile is untouched (the entry is
    /// deliberately outside it, so an imported profile cannot overwrite it).
    /// The command line points at the currently running exe with the --startup
    /// flag; an unresolvable exe path is a refusal log line, never a throw into
    /// the dialog.</summary>
    public void CommitAutostart(bool enabled)
    {
        if (!enabled)
        {
            autostartStore.SetCommandLine(null);
            startupLog.Write("Run entry removed (Start with Windows off)");
            return;
        }
        string? exePath = Environment.ProcessPath;
        if (exePath is null)
        {
            startupLog.Write("Run entry not written: the running exe path could not be resolved");
            return;
        }
        string commandLine = AutostartPolicy.BuildCommandLine(exePath);
        autostartStore.SetCommandLine(commandLine);
        startupLog.Write(() => $"Run entry written ({commandLine})");
    }

    /// <summary>The kill-switch write-through (ADR-0019): the checkbox's check
    /// is the change, so the machine-local setting commits in place (the
    /// profile is untouched - the switch lives beside it) and the idempotent
    /// registration pass re-runs: a tripped switch vetoes every registration,
    /// a released one re-registers the profile's chords.</summary>
    public void CommitKillSwitch(bool killSwitch)
    {
        var updated = appSettingsProvider() with { KillSwitch = killSwitch };
        saveAppSettings(updated);
        hotkeyLog.Write(() => killSwitch
            ? "Kill switch tripped: global hotkeys unregistered, AHK spawning disabled"
            : "Kill switch released: re-registering the profile's global hotkeys");
        refreshGlobalHotkeys();
    }

    /// <summary>The AHK interpreter write-through (ADR-0019): the machine-local
    /// path commits in place (the Run AHK Script action resolves it at spawn
    /// time) and the registration pass re-runs (the interpreter path is one of
    /// the documented triggers, so the pass is idempotently safe).</summary>
    public void CommitAhkInterpreter(string path)
    {
        var updated = appSettingsProvider() with { AhkInterpreterPath = path.Trim() };
        saveAppSettings(updated);
        refreshGlobalHotkeys();
    }

    /// <summary>The minimize-to-tray-on-startup write-through: the machine-local
    /// flag commits in place (the next launch reads it at construction time and
    /// opens hidden behind the tray icon). No registration pass re-runs - the
    /// flag has no effect on the live session, only on the next process start.</summary>
    public void CommitMinimizeToTrayOnStartup(bool enabled)
    {
        var updated = appSettingsProvider() with { MinimizeToTrayOnStartup = enabled };
        saveAppSettings(updated);
        startupLog.Write(() => enabled
            ? "Minimize to tray on startup: ON (next launch opens hidden)"
            : "Minimize to tray on startup: OFF (next launch opens normally)");
    }
}
