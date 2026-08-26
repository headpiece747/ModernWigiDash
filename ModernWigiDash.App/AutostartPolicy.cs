namespace ModernWigiDash.App;

/// <summary>
/// The Start-with-Windows policy (ADR-0019): the one HKCU Run entry the app
/// owns and the command line it carries. The registry is the single source
/// of truth: the settings checkbox reads the entry's presence, and the toggle
/// writes or deletes the value (machine-local, never in the profile, so an
/// imported profile cannot overwrite it). The command line points at the
/// currently running exe with the <see cref="StartupLaunchPolicy.StartupMinimizedArg"/>
/// flag, so the autostarted instance opens minimized. A dev-to-release exe
/// swap is not self-healing (the vendor Manager's entry has the same edge);
/// a re-toggle rewrites the path.
/// </summary>
internal static class AutostartPolicy
{
    /// <summary>The Run subkey the entry lives in (under HKCU: per-user
    /// autostart without elevation; HKLM would require an elevated write).</summary>
    public const string RunSubKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The Run value name the app owns.</summary>
    public const string RunValueName = "ModernWigiDash";

    /// <summary>
    /// The command line the Run entry carries: the quoted exe path plus the
    /// autostart flag. The quoting is unconditional (a Program Files path
    /// carries a space), so a bare path and a quoted path cannot drift.
    /// </summary>
    public static string BuildCommandLine(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        return $"\"{exePath}\" {StartupLaunchPolicy.StartupMinimizedArg}";
    }
}
