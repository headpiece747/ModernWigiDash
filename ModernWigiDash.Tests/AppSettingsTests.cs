using System.IO;

namespace ModernWigiDash.Tests;

/// <summary>
/// The app_settings.json store pinned against a temp path: the save/load
/// round trip (the kill switch + the interpreter path), the absent-file
/// default, the corrupt-file repair (the absent-service house pattern: a bad
/// machine-local file degrades to the defaults, never throws), and the
/// atomic save's residue rule (no .tmp left behind).
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
    public void Save_CreatesTheDirectory_WhenAbsent()
    {
        string path = StorePath();
        var store = new AppSettingsStore(path);

        store.Save(new AppSettings { KillSwitch = true });

        Assert.IsTrue(File.Exists(path), "the directory is created on the first save");
        Assert.IsTrue(store.Load().KillSwitch);
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}
