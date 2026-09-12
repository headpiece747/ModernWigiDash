using System.IO;

namespace ModernWigiDash.Tests;

/// <summary>
/// The app_settings.json store pinned against a temp path: the save/load
/// round trip (the kill switch + the interpreter path), the absent-file
/// default, the corrupt-file repair (the absent-service house pattern: a bad
/// machine-local file degrades to the defaults, never throws), the
/// hand-edited null path's normalize to the blank default, the failed
/// write's absorb (one log line, the tmp litter removed), and the atomic
/// save's residue rule (no .tmp left behind).
/// </summary>
[TestClass]
public class AppSettingsTests
{
    private static string StorePath() => Path.Combine(Path.GetTempPath(), "wmd-appsettings-" + Guid.NewGuid().ToString("N"), "app_settings.json");

    [TestMethod]
    public void SaveThenLoad_RoundTripsTheKillSwitchAndTheInterpreterPath()
    {
        string path = StorePath();
        var store = new AppSettingsStore(path);

        store.Save(new AppSettings { KillSwitch = true, AhkInterpreterPath = @"C:\Tools\autohotkey.exe" });
        AppSettings loaded = store.Load();

        Assert.IsTrue(loaded.KillSwitch);
        Assert.AreEqual(@"C:\Tools\autohotkey.exe", loaded.AhkInterpreterPath);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public void Load_AbsentFile_ReturnsTheDefaults()
    {
        var store = new AppSettingsStore(StorePath());

        AppSettings loaded = store.Load();

        Assert.IsFalse(loaded.KillSwitch, "the kill switch defaults off (the vendor parity: the integration is live)");
        Assert.AreEqual("", loaded.AhkInterpreterPath, "the interpreter path defaults blank (nothing bundled or auto-detected)");
    }

    [TestMethod]
    public void Load_CorruptFile_RepairsToTheDefaultsWithOneLogLine()
    {
        string path = StorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");
        var lines = new List<string>();
        var store = new AppSettingsStore(path, log: lines.Add);

        AppSettings loaded = store.Load();

        Assert.IsFalse(loaded.KillSwitch);
        Assert.AreEqual("", loaded.AhkInterpreterPath);
        Assert.AreEqual(1, lines.Count, "the repair logs one line (visible, not silent)");
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public void Save_IsAtomic_NoTempResidueRemains()
    {
        string path = StorePath();
        var store = new AppSettingsStore(path);

        store.Save(new AppSettings());

        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".tmp"), "the tmp file is replaced into place, never left behind");
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public void Save_MoveFails_LogsOneLineAndRemovesTheTempFile()
    {
        string path = StorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A directory at the target makes the atomic move fail (an access
        // refusal the store must absorb, not throw into the wiring); the
        // write has already created the tmp file, so the cleanup's remove
        // is the pin.
        Directory.CreateDirectory(path);
        var lines = new List<string>();
        var store = new AppSettingsStore(path, log: lines.Add);

        store.Save(new AppSettings { KillSwitch = true });

        Assert.AreEqual(1, lines.Count, "the failed write logs one line (best-effort, never a throw into the wiring)");
        Assert.IsFalse(File.Exists(path + ".tmp"), "the failed move's tmp litter is removed best-effort");
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public void Load_HandEditedNullInterpreterPath_NormalizesToTheBlankDefault()
    {
        string path = StorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"KillSwitch\":true,\"AhkInterpreterPath\":null}");
        var store = new AppSettingsStore(path);

        AppSettings loaded = store.Load();

        Assert.IsTrue(loaded.KillSwitch, "the sibling field survives the normalize");
        Assert.AreEqual("", loaded.AhkInterpreterPath, "a null path normalizes to the blank default (the value contract holds past the boundary)");
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public void Save_CreatesTheDirectory_WhenAbsent()
    {
        string path = StorePath();
        var store = new AppSettingsStore(path);

        store.Save(new AppSettings { KillSwitch = true });

        Assert.IsTrue(File.Exists(path), "the directory is created on the first save");
        Assert.IsTrue(store.Load().KillSwitch);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public void DefaultPath_LivesInTheUserStateDirBesideTheProfile()
    {
        // The production default path is the user state dir (ADR-0021 shape):
        // %LOCALAPPDATA%\ModernWigiDash\app_settings.json, beside profile.json.
        string def = AppSettingsStore.DefaultPath();

        Assert.IsTrue(def.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            StringComparison.OrdinalIgnoreCase),
            "the default lives under the user state dir, not the exe dir");
        StringAssert.Contains(def, "ModernWigiDash", "the app's state folder names the dir");
        StringAssert.EndsWith(def, Path.Combine("ModernWigiDash", "app_settings.json"));
    }

    [TestMethod]
    public void Save_PathTooLong_AbsorbsTheFaultAndRemovesTheTempLitter()
    {
        // A path longer than MAX_PATH makes the write throw PathTooLongException;
        // the store must absorb it (one log line) and remove the temp litter
        // without throwing into the wiring. The long-path opt-in (Windows registry
        // or managed handling) can make an over-MAX_PATH path succeed instead of
        // throwing, so probe the path first and assert the log line only when the
        // path genuinely faults; the no-throw and no-litter invariants hold either
        // way.
        string longName = new string('x', 300);
        string path = Path.Combine(Path.GetTempPath(), longName, longName, "app_settings.json");
        bool pathFaults;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            pathFaults = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            pathFaults = true;
        }

        var lines = new List<string>();
        var store = new AppSettingsStore(path, log: lines.Add);

        store.Save(new AppSettings { KillSwitch = true }); // must not throw into the wiring

        if (pathFaults)
            Assert.AreEqual(1, lines.Count, "a faulting path logs exactly one line (best-effort, never a throw)");
        else
            Assert.AreEqual(0, lines.Count, "a non-faulting (long-path-enabled) path saves cleanly with no error line");
        Assert.IsFalse(File.Exists(path + ".tmp"), "no stale .tmp litter survives the save");
    }
}
