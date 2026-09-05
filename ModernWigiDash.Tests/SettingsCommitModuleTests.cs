namespace ModernWigiDash.Tests;

/// <summary>
/// The settings hub's commit module pinned at its interface (no window, no
/// dialog): each of the five write-throughs commits exactly its half - the
/// profile close behavior marks dirty, the autostart Run entry is written or
/// deleted (an unresolvable exe path refuses with a log line), the kill switch
/// and the interpreter path persist the machine-local setting AND re-run the
/// idempotent hotkey registration pass, and the minimize-to-tray flag persists
/// WITHOUT re-running the pass (it only affects the next launch).
/// </summary>
[TestClass]
public class SettingsCommitModuleTests
{
    private sealed class FakeAutostartStore : IAutostartStore
    {
        public string? CommandLine { get; private set; }
        public string? TryGetCommandLine() => CommandLine;
        public void SetCommandLine(string? commandLine) => CommandLine = commandLine;
    }

    private sealed class Harness
    {
        public ProfileLayout Profile { get; } = new();
        public List<string> DirtyMarks { get; } = [];
        public AppSettings AppSettings { get; set; } = new();
        public List<AppSettings> Saved { get; } = [];
        public int HotkeyRefreshes { get; set; }
        public List<string> StartupLog { get; } = [];
        public List<string> HotkeyLog { get; } = [];
        public FakeAutostartStore Autostart { get; } = new();
        public SettingsCommitModule Module { get; }

        public Harness()
        {
            var startup = new DiagLog("STARTUP", 1, true, StartupLog.Add);
            var hotkey = new DiagLog("HOTKEY", 1, true, HotkeyLog.Add);
            Module = new SettingsCommitModule(
                profileProvider: () => Profile,
                markDirty: () => DirtyMarks.Add("dirty"),
                appSettingsProvider: () => AppSettings,
                saveAppSettings: s => { AppSettings = s; Saved.Add(s); },
                autostartStore: Autostart,
                refreshGlobalHotkeys: () => HotkeyRefreshes++,
                startupLog: startup,
                hotkeyLog: hotkey);
        }
    }

    [TestMethod]
    public void CommitCloseBehavior_WritesTheProfileAndMarksDirty()
    {
        var h = new Harness();

        h.Module.CommitCloseBehavior("hideToTray");

        Assert.AreEqual("hideToTray", h.Profile.CloseBehavior);
        CollectionAssert.Contains(h.DirtyMarks, "dirty");
    }

    [TestMethod]
    public void CommitAutostart_Disabled_DeletesTheRunEntry()
    {
        var h = new Harness();

        h.Module.CommitAutostart(false);

        Assert.IsNull(h.Autostart.CommandLine);
        StringAssert.Contains(h.StartupLog[0], "removed");
    }

    [TestMethod]
    public void CommitAutostart_Enabled_WritesTheQuotedCommandLine()
    {
        var h = new Harness();

        h.Module.CommitAutostart(true);

        // Environment.ProcessPath is the running test host exe; the command line
        // quotes it and rides the --startup flag.
        Assert.IsNotNull(h.Autostart.CommandLine);
        StringAssert.Contains(h.Autostart.CommandLine, "--startup");
        StringAssert.Contains(h.StartupLog[0], "written");
    }

    [TestMethod]
    public void CommitKillSwitch_PersistsAndReRunsTheRegistrationPass()
    {
        var h = new Harness();

        h.Module.CommitKillSwitch(true);

        Assert.IsTrue(h.AppSettings.KillSwitch);
        CollectionAssert.Contains(h.Saved, h.AppSettings);
        Assert.AreEqual(1, h.HotkeyRefreshes);
        StringAssert.Contains(h.HotkeyLog[0], "tripped");
    }

    [TestMethod]
    public void CommitAhkInterpreter_PersistsTrimmedAndReRunsThePass()
    {
        var h = new Harness();

        h.Module.CommitAhkInterpreter(@"  C:\Tools\autohotkey.exe  ");

        Assert.AreEqual(@"C:\Tools\autohotkey.exe", h.AppSettings.AhkInterpreterPath);
        Assert.AreEqual(1, h.HotkeyRefreshes);
    }

    [TestMethod]
    public void CommitMinimizeToTrayOnStartup_PersistsWithoutReRunningThePass()
    {
        var h = new Harness();

        h.Module.CommitMinimizeToTrayOnStartup(true);

        Assert.IsTrue(h.AppSettings.MinimizeToTrayOnStartup);
        CollectionAssert.Contains(h.Saved, h.AppSettings);
        Assert.AreEqual(0, h.HotkeyRefreshes, "the flag only affects the next launch, not the live session");
        StringAssert.Contains(h.StartupLog[0], "ON");
    }
}
