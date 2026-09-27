using System.IO;
using ModernWigiDash.App;
using ModernWigiDash.App.Hotkey;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.Tests;

/// <summary>
/// The AutoHotkey spawn policy's own test surface (ADR-0019): the refusal ladder
/// (kill switch → blank script → unset interpreter → missing interpreter), the
/// launch-failure line, and the success path.
///
/// These five cases used to run through a live STA window — an XAML tree, a
/// profile load, a telemetry session, a tray icon, a temp settings store, a temp
/// profile and a swapped FileLog path per test — to assert two things the policy
/// already owns: how many times the spawn seam was called, and which refusal line
/// was logged. The policy takes its settings, its interpreter-existence probe and
/// its launch seam as arguments, so none of that startup was ever load-bearing
/// (the architecture review's "wire nothing" seam candidate, 2026-09-27).
///
/// What the window still has to prove is the WIRING — that the window's launch
/// entry point reaches this policy — and that one case stayed in
/// <see cref="WindowAhkScriptTests"/>. This class asserts policy behaviour only,
/// which is why it must never build a window (pinned).
/// </summary>
[TestClass]
public class AhkSpawnPolicyTests
{
    private const string ScriptPath = @"C:\scripts\fan.ahk";
    private const string PresentInterpreter = @"C:\AutoHotkey\AutoHotkey.exe";

    // A recorder over the launch seam: counts the spawns and captures the
    // (interpreter, script) pair, so no real interpreter is ever executed.
    private readonly List<(string Interpreter, string Script)> _spawns = [];
    private readonly AhkLaunchApi _api;
    private string? _originalLogPath;
    private string _logDir = "";

    public AhkSpawnPolicyTests()
    {
        _api = new AhkLaunchApi((interpreter, script) =>
        {
            _spawns.Add((interpreter, script));
            return true;
        });
    }

    [TestInitialize]
    public void SetUp()
    {
        // The log is redirected per test so the policy's lines are assertable
        // without the window that used to own the tag. The suite-wide pin keeps
        // this from ever reaching the user's real display log.
        _logDir = Path.Combine(Path.GetTempPath(), "wmd-ahk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_logDir);
        _originalLogPath = FileLog.LogPath;
        FileLog.LogPath = Path.Combine(_logDir, "display_device.log");
    }

    [TestCleanup]
    public void Cleanup()
    {
        FileLog.LogPath = _originalLogPath ?? FileLog.LogPath;
        try { Directory.Delete(_logDir, true); }
        catch (IOException) { /* best-effort: FileLog may still hold the stream */ }
    }

    private AhkSpawnPolicy Policy(bool interpreterExists = true)
        => new(_api, new DiagLog("HOTKEY", 1), fileExists: _ => interpreterExists);

    private string ReadLog()
    {
        FileLog.Flush();
        // FileShare.ReadWrite: FileLog holds its own handle while the path is assigned.
        using var stream = new FileStream(FileLog.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [TestMethod]
    public void TryLaunch_KillSwitchChecked_RefusesWithoutSpawning()
    {
        var launched = Policy().TryLaunch(ScriptPath, new AppSettings
        {
            KillSwitch = true,
            AhkInterpreterPath = PresentInterpreter,
        });

        Assert.IsFalse(launched, "a tripped kill switch never spawns");
        Assert.AreEqual(0, _spawns.Count);
        StringAssert.Contains(ReadLog(), "[HOTKEY] AHK spawn refused: the kill switch is checked (Settings)");
    }

    [TestMethod]
    public void TryLaunch_BlankScriptPath_RefusesWithoutSpawning()
    {
        // A present interpreter isolates the refusal to the blank script: without
        // the guard the spawn would run a pointless interpreter.
        var launched = Policy().TryLaunch("   ", new AppSettings { AhkInterpreterPath = PresentInterpreter });

        Assert.IsFalse(launched, "a blank script path never spawns (a pointless interpreter)");
        Assert.AreEqual(0, _spawns.Count);
        StringAssert.Contains(ReadLog(), "[HOTKEY] AHK spawn refused: no script path set (the widget's command is blank)");
    }

    [TestMethod]
    public void TryLaunch_NoInterpreterSet_RefusesWithoutSpawning()
    {
        var launched = Policy().TryLaunch(ScriptPath, new AppSettings());

        Assert.IsFalse(launched, "an unset interpreter never spawns");
        Assert.AreEqual(0, _spawns.Count);
        StringAssert.Contains(ReadLog(), "[HOTKEY] AHK spawn refused: no AutoHotkey interpreter path set (Settings)");
    }

    [TestMethod]
    public void TryLaunch_InterpreterMissing_RefusesWithoutSpawning()
    {
        const string missing = @"C:\nope\definitely-absent.exe";

        var launched = Policy(interpreterExists: false)
            .TryLaunch(ScriptPath, new AppSettings { AhkInterpreterPath = missing });

        Assert.IsFalse(launched, "a missing interpreter never spawns");
        Assert.AreEqual(0, _spawns.Count);
        StringAssert.Contains(ReadLog(), $"[HOTKEY] AHK spawn refused: interpreter not found: {missing}");
    }

    [TestMethod]
    public void TryLaunch_ValidSettings_SpawnsTheUsersInterpreterOnce()
    {
        var launched = Policy().TryLaunch(ScriptPath, new AppSettings { AhkInterpreterPath = PresentInterpreter });

        Assert.IsTrue(launched, "a valid interpreter and script launch once");
        Assert.AreEqual(1, _spawns.Count, "the spawn runs exactly once");
        Assert.AreEqual(PresentInterpreter, _spawns[0].Interpreter, "the interpreter is the user's settings path");
        Assert.AreEqual(ScriptPath, _spawns[0].Script, "the script path is the action's command value");
        StringAssert.Contains(ReadLog(), $"[HOTKEY] AHK launched: {ScriptPath}");
    }

    [TestMethod]
    public void TryLaunch_SettingsAreReadPerCall_NotCaptured()
    {
        // The settings arrive per call, which is what makes a settings
        // write-through visible on the next action without a restart (the
        // window's live-settings behaviour, asserted at the seam that owns it).
        var policy = Policy();
        var killSwitched = new AppSettings { KillSwitch = true, AhkInterpreterPath = PresentInterpreter };
        var open = new AppSettings { KillSwitch = false, AhkInterpreterPath = PresentInterpreter };

        Assert.IsFalse(policy.TryLaunch(ScriptPath, killSwitched), "the kill switch refuses");
        Assert.IsTrue(policy.TryLaunch(ScriptPath, open), "the same policy instance launches once the switch is off");
        Assert.AreEqual(1, _spawns.Count, "exactly the un-refused call reached the seam");
    }

    [TestMethod]
    public void TryLaunch_LaunchFails_LogsTheFailureLineAndReturnsFalse()
    {
        // The last rung of the ladder: the seam refuses, so the policy reports a
        // failed launch rather than a launched one.
        var failing = new AhkLaunchApi((_, _) => false);

        var launched = new AhkSpawnPolicy(failing, new DiagLog("HOTKEY", 1), fileExists: _ => true)
            .TryLaunch(ScriptPath, new AppSettings { AhkInterpreterPath = PresentInterpreter });

        Assert.IsFalse(launched, "a refused spawn seam is not a launch");
        string log = ReadLog();
        StringAssert.Contains(log, $"[HOTKEY] AHK spawn failed: {PresentInterpreter}");
        Assert.IsFalse(
            log.Contains("AHK launched:", StringComparison.Ordinal),
            "a failed launch must not also log the success line");
    }
}
