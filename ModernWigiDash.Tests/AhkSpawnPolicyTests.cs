using ModernWigiDash.App.Hotkey;

namespace ModernWigiDash.Tests;

/// <summary>
/// The AHK spawn policy pinned at its interface without a window or a real
/// interpreter: the refusal ladder (kill switch â†’ blank script â†’ unset
/// interpreter â†’ missing interpreter) each logs one line and stops, a
/// successful launch logs the launch line and returns true, and a failed
/// launch logs the failure line. The file-exists probe is injected so the
/// missing-interpreter leg is drivable without the filesystem.
/// </summary>
[TestClass]
public class AhkSpawnPolicyTests
{
    private static (AhkSpawnPolicy Policy, List<string> Lines, List<(string Interpreter, string Script)> Launches) Build(
        bool fileExists = true,
        bool launchSucceeds = true)
    {
        var lines = new List<string>();
        var log = new DiagLog("TEST", 1, write: lines.Add);
        var launches = new List<(string, string)>();
        var api = new AhkLaunchApi((i, s) => { launches.Add((i, s)); return launchSucceeds; });
        var policy = new AhkSpawnPolicy(api, log, _ => fileExists);
        return (policy, lines, launches);
    }

    [TestMethod]
    public void TryLaunch_KillSwitch_RefusesWithOneLineAndNoLaunch()
    {
        var (policy, lines, launches) = Build();

        bool result = policy.TryLaunch(@"C:\scripts\test.ahk", new AppSettings { KillSwitch = true, AhkInterpreterPath = @"C:\ahk\autohotkey.exe" });

        Assert.IsFalse(result);
        Assert.AreEqual(0, launches.Count, "the kill switch must veto before any launch");
        StringAssert.Contains(lines[0], "kill switch");
    }

    [TestMethod]
    public void TryLaunch_BlankScript_RefusesWithOneLineAndNoLaunch()
    {
        var (policy, lines, launches) = Build();

        bool result = policy.TryLaunch("", new AppSettings { KillSwitch = false, AhkInterpreterPath = @"C:\ahk\autohotkey.exe" });

        Assert.IsFalse(result);
        Assert.AreEqual(0, launches.Count);
        StringAssert.Contains(lines[0], "no script path");
    }

    [TestMethod]
    public void TryLaunch_UnsetInterpreter_RefusesWithOneLineAndNoLaunch()
    {
        var (policy, lines, launches) = Build();

        bool result = policy.TryLaunch(@"C:\scripts\test.ahk", new AppSettings { KillSwitch = false, AhkInterpreterPath = "" });

        Assert.IsFalse(result);
        Assert.AreEqual(0, launches.Count);
        StringAssert.Contains(lines[0], "no AutoHotkey interpreter path");
    }

    [TestMethod]
    public void TryLaunch_MissingInterpreter_RefusesWithOneLineAndNoLaunch()
    {
        var (policy, lines, launches) = Build(fileExists: false);

        bool result = policy.TryLaunch(@"C:\scripts\test.ahk", new AppSettings { KillSwitch = false, AhkInterpreterPath = @"C:\ahk\autohotkey.exe" });

        Assert.IsFalse(result);
        Assert.AreEqual(0, launches.Count);
        StringAssert.Contains(lines[0], "interpreter not found");
    }

    [TestMethod]
    public void TryLaunch_LaunchFails_LogsFailureAndReturnsFalse()
    {
        var (policy, lines, launches) = Build(launchSucceeds: false);

        bool result = policy.TryLaunch(@"C:\scripts\test.ahk", new AppSettings { KillSwitch = false, AhkInterpreterPath = @"C:\ahk\autohotkey.exe" });

        Assert.IsFalse(result);
        Assert.AreEqual(1, launches.Count, "the launch was attempted");
        StringAssert.Contains(lines[^1], "AHK spawn failed");
    }

    [TestMethod]
    public void TryLaunch_Success_LogsLaunchLineAndReturnsTrue()
    {
        var (policy, lines, launches) = Build();

        bool result = policy.TryLaunch(@"C:\scripts\test.ahk", new AppSettings { KillSwitch = false, AhkInterpreterPath = @"C:\ahk\autohotkey.exe" });

        Assert.IsTrue(result);
        Assert.AreEqual(1, launches.Count);
        Assert.AreEqual(@"C:\ahk\autohotkey.exe", launches[0].Interpreter);
        Assert.AreEqual(@"C:\scripts\test.ahk", launches[0].Script);
        StringAssert.Contains(lines[^1], "AHK launched");
    }
}
