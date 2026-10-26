using System.Diagnostics;
using ModernWigiDash.Widgets.Steam.Models;

namespace ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The Steam process seam: a live query of the running process list and a
/// bounded close of a named process tree. The production implementation is
/// <see cref="SteamProcessBridge"/> (System.Diagnostics Process); tests
/// inject a fake so the tracker's state fold and launch/close logic run
/// deterministically without a real process.
/// </summary>
internal interface ISteamProcessBridge
{
    /// <summary>Whether a process named <paramref name="processName"/> is running right now.</summary>
    bool IsRunning(string processName);

    /// <summary>
    /// Closes every process named <paramref name="processName"/>: a polite
    /// CloseMainWindow, a bounded wait up to <paramref name="budget"/>, then a
    /// Kill(entireProcessTree: true) on whatever is still alive. True when
    /// nothing is left running (including when nothing was found).
    /// </summary>
    bool TryClose(string processName, TimeSpan budget);
}

/// <summary>
/// The production <see cref="ISteamProcessBridge"/> over
/// System.Diagnostics.Process. Every entry degrades gracefully: an empty
/// name, an unreadable process table, or a process that vanished mid-close
/// reads as "nothing to do", never a throw into the caller.
/// </summary>
internal sealed class SteamProcessBridge : ISteamProcessBridge
{
    /// <summary>How long a process-name snapshot is trusted: one 3-second tick's
    /// per-game checks share a single enumeration instead of one per game.</summary>
    private static readonly TimeSpan NameCacheTtl = TimeSpan.FromMilliseconds(500);

    private readonly TimeProvider _clock;
    private HashSet<string>? _runningNames;
    private DateTimeOffset _runningNamesUtc;

    /// <summary>The production bridge over the system clock.</summary>
    public SteamProcessBridge() : this(TimeProvider.System)
    {
    }

    /// <summary>Test seam: an injectable clock (the close-wait deadline is clock-relative).</summary>
    internal SteamProcessBridge(TimeProvider clock)
    {
        _clock = clock;
    }

    public bool IsRunning(string processName)
    {
        if (processName.Length == 0)
            return false;

        try
        {
            DateTimeOffset now = _clock.GetUtcNow();
            if (_runningNames is null || now - _runningNamesUtc > NameCacheTtl)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Process process in Process.GetProcesses())
                {
                    using (process)
                    {
                        string? name = SafeProcessName(process);
                        if (name is not null)
                            names.Add(name);
                    }
                }

                _runningNames = names;
                _runningNamesUtc = now;
            }

            return _runningNames.Contains(processName);
        }
        catch (Exception)
        {
            // The process table is unreadable (a blocked enumeration): the
            // state reads as not running, never a throw.
            return false;
        }
    }

    /// <summary>
    /// One process's name, or null when the process denies it (a protected
    /// process cannot match a game, so it simply does not join the snapshot).
    /// </summary>
    private static string? SafeProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (Exception)
        {
            // A protected process denies its name; it cannot match a game.
            return null;
        }
    }

    public bool TryClose(string processName, TimeSpan budget)
    {
        if (processName.Length == 0)
            return false;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception)
        {
            // The process table is unreadable: there is nothing to close.
            return true;
        }

        if (processes.Length == 0)
            return true; // nothing to close.

        try
        {
            // The polite leg: a CloseMainWindow to every live process. A
            // windowless process (a service, a headless launch) cannot take
            // one; the kill leg below covers it.
            foreach (Process process in processes)
            {
                if (process.HasExited)
                    continue;

                try
                {
                    process.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                    // The process exited between the HasExited check and the
                    // close; the exit poll below reads it as gone.
                }
            }

            // The bounded wait: poll for a graceful exit up to the budget.
            DateTimeOffset deadline = _clock.GetUtcNow().Add(budget);
            bool allExited = true;
            while (!allExited && _clock.GetUtcNow() < deadline)
            {
                allExited = true;
                foreach (Process process in processes)
                {
                    if (!process.HasExited)
                    {
                        allExited = false;
                        break;
                    }
                }

                if (!allExited)
                    Thread.Sleep(100);
            }

            // The kill leg: whatever is still alive gets its whole tree
            // taken down (the PRD's CloseMainWindow -> 3s -> Kill contract).
            if (!allExited)
            {
                foreach (Process process in processes)
                {
                    if (process.HasExited)
                        continue;

                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited between the check and the kill;
                        // it is already gone.
                    }
                }
            }

            return true;
        }
        catch (Exception)
        {
            // A fault in the close legs (a process in a bad state) degrades to
            // "nothing left running", never a throw into the caller (the
            // fire-and-forget terminate task must not swallow a fault silently).
            return true;
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }
}

/// <summary>
/// Steam process tracking: the fold of a manifest's ACF flags and the live
/// process check into the four on-screen states (Running beats Updating beats
/// Idle beats Uninstalled), the steam://run launch URI, and the launch
/// (shell-exec sink) and terminate (process bridge) seams. Stateless across
/// calls; the 3-second re-resolution lives in the service loop that calls
/// <see cref="Resolve"/> each tick. <see cref="TryClose(string)"/> blocks up to
/// the close budget, so a caller must run it off the render thread.
/// </summary>
internal sealed class SteamProcessTracker
{
    /// <summary>The close budget: the polite close, then up to 3 seconds, then the kill.</summary>
    public static readonly TimeSpan CloseBudget = TimeSpan.FromSeconds(3);

    private readonly ISteamProcessBridge _bridge;
    private readonly Action<string> _launchSink;

    /// <summary>
    /// The tracker over an injected process bridge and shell-exec launch
    /// sink (the sink opens the steam://run URI through the OS handler).
    /// </summary>
    public SteamProcessTracker(ISteamProcessBridge bridge, Action<string> launchSink)
    {
        _bridge = bridge;
        _launchSink = launchSink;
    }

    /// <summary>
    /// The state fold: a live process is Running, an active-update flag is
    /// Updating, a fully-installed app is Idle, and anything else is
    /// Uninstalled. A manifest with no executable fact cannot be matched to
    /// a process, so it folds on the flags alone.
    /// </summary>
    public SteamGameState Resolve(SteamAppManifest manifest)
    {
        if (manifest.ProcessName.Length > 0 && _bridge.IsRunning(manifest.ProcessName))
            return SteamGameState.Running;

        // The ACF's AppRunning bit is the fallback for a manifest with no
        // resolvable executable name; it is only trusted when the live
        // process check cannot be performed, because a stale flag would
        // otherwise pin a crashed game to Running forever.
        if (manifest.ProcessName.Length == 0 && manifest.StateFlags.HasFlag(SteamAppState.AppRunning))
            return SteamGameState.Running;

        if (manifest.IsUpdating)
            return SteamGameState.Updating;

        if (manifest.IsInstalled)
            return SteamGameState.Idle;

        return SteamGameState.Uninstalled;
    }

    /// <summary>
    /// The steam://run launch URI for an app id. The id is a uint, so the
    /// URI carries only digits by construction (no injection surface).
    /// </summary>
    public static string BuildLaunchUri(uint appId) => $"steam://run/{appId}";

    /// <summary>Launches the game through the shell-exec sink (the steam://run URI).</summary>
    public void Launch(uint appId) => _launchSink(BuildLaunchUri(appId));

    /// <summary>
    /// Terminates a process by name (the widget's tap-on-a-running-title path,
    /// where the display item carries the app id and the service owns the
    /// app-id → process-name map). Blocks up to <see cref="CloseBudget"/>;
    /// callers must run it off the render thread.
    /// </summary>
    public bool TryClose(string processName) => _bridge.TryClose(processName, CloseBudget);
}
