using System.IO;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.Hotkey;

/// <summary>
/// The AutoHotkey spawn policy (ADR-0019): the one owner of the refusal checks
/// and their log lines for a user-supplied AHK script launch. Before this
/// module the four refusals (kill switch, blank script, unset interpreter,
/// missing interpreter) were spelled inline in the window's context partial;
/// now they concentrate here with their own test surface, driving the existing
/// <see cref="AhkLaunchApi"/> seam. The settings are read live at call time (a
/// settings write-through is seen on the next action without a restart); the
/// interpreter existence check uses an injectable probe so tests drive it
/// without the filesystem.
/// </summary>
internal sealed class AhkSpawnPolicy(AhkLaunchApi api, DiagLog log, Func<string, bool>? fileExists = null)
{
    private readonly AhkLaunchApi _api = api;
    private readonly DiagLog _log = log;
    private readonly Func<string, bool> _fileExists = fileExists ?? File.Exists;

    /// <summary>
    /// Attempts to launch the named script with the user's interpreter, applying
    /// the refusal ladder in order (kill switch → blank script → unset
    /// interpreter → missing interpreter). Each refusal logs one line and stops;
    /// a successful launch logs the launch line. Returns true when the process
    /// started, false when refused or the launch failed.
    /// </summary>
    public bool TryLaunch(string scriptPath, AppSettings settings)
    {
        if (settings.KillSwitch)
        {
            _log.Write("AHK spawn refused: the kill switch is checked (Settings)");
            return false;
        }
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            _log.Write("AHK spawn refused: no script path set (the widget's command is blank)");
            return false;
        }
        if (string.IsNullOrWhiteSpace(settings.AhkInterpreterPath))
        {
            _log.Write("AHK spawn refused: no AutoHotkey interpreter path set (Settings)");
            return false;
        }

        string interpreter = settings.AhkInterpreterPath;
        if (!_fileExists(interpreter))
        {
            _log.Write($"AHK spawn refused: interpreter not found: {interpreter}");
            return false;
        }
        if (!_api.Launch(interpreter, scriptPath))
        {
            _log.Write($"AHK spawn failed: {interpreter}");
            return false;
        }
        _log.Write($"AHK launched: {scriptPath}");
        return true;
    }
}
