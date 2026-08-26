using System.Diagnostics;

namespace ModernWigiDash.App.Hotkey;

/// <summary>
/// The AutoHotkey spawn seam (the HotkeyApi delegate-bag precedent): one
/// launch (interpreter path + script path, true when the process started).
/// Production binds <c>Process.Start</c>; tests bind a recorder so
/// the spawn policy (the kill-switch veto, the interpreter checks, the
/// launch line) is drivable without a real interpreter (ADR-0019: the
/// interpreter is user-supplied, nothing is bundled).
/// </summary>
internal sealed record AhkLaunchApi(Func<string, string, bool> Launch)
{
    /// <summary>The production binding: a bare spawn, no tracking.</summary>
    public static readonly AhkLaunchApi Default = new((interpreter, script) =>
    {
        try
        {
            // ArgumentList quotes the script path (spaces and all); the
            // interpreter is the user's own path, resolved by the caller.
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = interpreter,
                ArgumentList = { script },
                UseShellExecute = false
            });
            return process is not null;
        }
        catch (Exception)
        {
            // The caller logs the failure line (it owns the settings
            // context); a failed spawn is a false, never a throw.
            return false;
        }
    });
}
