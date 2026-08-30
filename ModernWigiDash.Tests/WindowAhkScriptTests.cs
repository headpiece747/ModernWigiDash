using System.IO;
using ModernWigiDash.App.Hotkey;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's AutoHotkey spawn policy pinned on a live STA window
/// (ADR-0019): the kill-switch veto, the blank-script refusal, the
/// unset-interpreter refusal, the missing-interpreter refusal, and the
/// success path (the user's settings interpreter + the script path ride
/// the spawn seam exactly once, with the launch line). The spawn seam is
/// the injected recorder, so no real interpreter runs; the app-settings
/// store is a temp file, so the pin never touches the user's
/// machine-local settings.
/// </summary>
[TestClass]
public class WindowAhkScriptTests
{
    private const string ScriptPath = @"C:\scripts\fan.ahk";

    private static readonly StaHost Host = new("WindowAhkScript-STA");

    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }

    [TestMethod]
    public void LaunchAutoHotkeyScript_KillSwitchChecked_RefusesWithoutSpawning()
    {
        var (spawns, ahkApi) = Recorder();
        string settingsDir = CreateTempDir();
        var (store, logDir) = SeedStoreAndLog(new AppSettings { KillSwitch = true, AhkInterpreterPath = @"C:\AutoHotkey\AutoHotkey.exe" }, settingsDir);
        string profilePath = SeedProfile();
        string originalLogPath = FileLog.LogPath;
        Host.Run<object?>(() =>
        {
            FileLog.LogPath = Path.Combine(logDir, "display_device.log");
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), HotkeyApi: new FakeHotkeyApi().Api, AhkApi: ahkApi, AppSettingsStore: store, UsbEngine: FakeTransport.InertEngine()));
            try
            {
                window.LaunchAutoHotkeyScript(ScriptPath);
                FileLog.Flush();

                Assert.AreEqual(0, spawns.Count, "a tripped kill switch never spawns");
                StringAssert.Contains(ReadLog(FileLog.LogPath), "[HOTKEY] AHK spawn refused: the kill switch is checked (Settings)");
            }
            finally
            {
                FileLog.LogPath = originalLogPath;
                window.QuitClose();
                DeleteDirs(settingsDir, logDir);
            }
            return null;
        });
    }

    [TestMethod]
    public void LaunchAutoHotkeyScript_NoInterpreterSet_RefusesWithoutSpawning()
    {
        var (spawns, ahkApi) = Recorder();
        string settingsDir = CreateTempDir();
        var (store, logDir) = SeedStoreAndLog(new AppSettings(), settingsDir);
        string profilePath = SeedProfile();
        string originalLogPath = FileLog.LogPath;
        Host.Run<object?>(() =>
        {
            FileLog.LogPath = Path.Combine(logDir, "display_device.log");
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), HotkeyApi: new FakeHotkeyApi().Api, AhkApi: ahkApi, AppSettingsStore: store, UsbEngine: FakeTransport.InertEngine()));
            try
            {
                window.LaunchAutoHotkeyScript(ScriptPath);
                FileLog.Flush();

                Assert.AreEqual(0, spawns.Count, "an unset interpreter never spawns");
                StringAssert.Contains(ReadLog(FileLog.LogPath), "[HOTKEY] AHK spawn refused: no AutoHotkey interpreter path set (Settings)");
            }
            finally
            {
                FileLog.LogPath = originalLogPath;
                window.QuitClose();
                DeleteDirs(settingsDir, logDir);
            }
            return null;
        });
    }

    [TestMethod]
    public void LaunchAutoHotkeyScript_InterpreterMissing_RefusesWithoutSpawning()
    {
        var (spawns, ahkApi) = Recorder();
        string settingsDir = CreateTempDir();
        string missing = Path.Combine(settingsDir, "definitely-absent.exe");
        var (store, logDir) = SeedStoreAndLog(new AppSettings { AhkInterpreterPath = missing }, settingsDir);
        string profilePath = SeedProfile();
        string originalLogPath = FileLog.LogPath;
        Host.Run<object?>(() =>
        {
            FileLog.LogPath = Path.Combine(logDir, "display_device.log");
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), HotkeyApi: new FakeHotkeyApi().Api, AhkApi: ahkApi, AppSettingsStore: store, UsbEngine: FakeTransport.InertEngine()));
            try
            {
                window.LaunchAutoHotkeyScript(ScriptPath);
                FileLog.Flush();

                Assert.AreEqual(0, spawns.Count, "a missing interpreter never spawns");
                StringAssert.Contains(ReadLog(FileLog.LogPath), $"[HOTKEY] AHK spawn refused: interpreter not found: {missing}");
            }
            finally
            {
                FileLog.LogPath = originalLogPath;
                window.QuitClose();
                DeleteDirs(settingsDir, logDir);
            }
            return null;
        });
    }

    [TestMethod]
    public void LaunchAutoHotkeyScript_BlankScriptPath_RefusesWithoutSpawning()
    {
        var (spawns, ahkApi) = Recorder();
        string settingsDir = CreateTempDir();
        // A valid interpreter path isolates the refusal to the blank
        // script: without the guard the spawn would run a pointless
        // interpreter.
        string interpreter = Path.Combine(settingsDir, "autohotkey.exe");
        File.WriteAllText(interpreter, "dummy interpreter (the fake spawn seam never executes it)");
        var (store, logDir) = SeedStoreAndLog(new AppSettings { AhkInterpreterPath = interpreter }, settingsDir);
        string profilePath = SeedProfile();
        string originalLogPath = FileLog.LogPath;
        Host.Run<object?>(() =>
        {
            FileLog.LogPath = Path.Combine(logDir, "display_device.log");
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), HotkeyApi: new FakeHotkeyApi().Api, AhkApi: ahkApi, AppSettingsStore: store, UsbEngine: FakeTransport.InertEngine()));
            try
            {
                window.LaunchAutoHotkeyScript("   ");
                FileLog.Flush();

                Assert.AreEqual(0, spawns.Count, "a blank script path never spawns (a pointless interpreter)");
                StringAssert.Contains(ReadLog(FileLog.LogPath), "[HOTKEY] AHK spawn refused: no script path set (the widget's command is blank)");
            }
            finally
            {
                FileLog.LogPath = originalLogPath;
                window.QuitClose();
                DeleteDirs(settingsDir, logDir);
            }
            return null;
        });
    }

    [TestMethod]
    public void LaunchAutoHotkeyScript_LiveSettings_SpawnsTheUsersInterpreterOnce()
    {
        var (spawns, ahkApi) = Recorder();
        string settingsDir = CreateTempDir();
        // The interpreter path must exist for the spawn to run; the dummy
        // file is never executed (the injected seam records instead).
        string interpreter = Path.Combine(settingsDir, "autohotkey.exe");
        File.WriteAllText(interpreter, "dummy interpreter (the fake spawn seam never executes it)");
        var (store, logDir) = SeedStoreAndLog(new AppSettings { AhkInterpreterPath = interpreter }, settingsDir);
        string profilePath = SeedProfile();
        string originalLogPath = FileLog.LogPath;
        Host.Run<object?>(() =>
        {
            FileLog.LogPath = Path.Combine(logDir, "display_device.log");
            var window = new MainWindow(new MainWindowTestOptions(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), HotkeyApi: new FakeHotkeyApi().Api, AhkApi: ahkApi, AppSettingsStore: store, UsbEngine: FakeTransport.InertEngine()));
            try
            {
                window.LaunchAutoHotkeyScript(ScriptPath);
                FileLog.Flush();

                Assert.AreEqual(1, spawns.Count, "the spawn runs exactly once");
                Assert.AreEqual(interpreter, spawns[0].Interpreter, "the interpreter is the user's settings path");
                Assert.AreEqual(ScriptPath, spawns[0].Script, "the script path is the action's command value");
                StringAssert.Contains(ReadLog(FileLog.LogPath), $"[HOTKEY] AHK launched: {ScriptPath}");
            }
            finally
            {
                FileLog.LogPath = originalLogPath;
                window.QuitClose();
                DeleteDirs(settingsDir, logDir);
            }
            return null;
        });
    }

    /// <summary>Seeds a temp app-settings file through the store's own
    /// save (the atomic write path) and returns the store (the window's
    /// wiring step loads it) + a sibling temp dir for the log.</summary>
    private static (AppSettingsStore Store, string LogDir) SeedStoreAndLog(AppSettings settings, string settingsDir)
    {
        var store = new AppSettingsStore(Path.Combine(settingsDir, "app_settings.json"));
        store.Save(settings);
        return (store, CreateTempDir());
    }

    private static string SeedProfile()
    {
        string dir = CreateTempDir();
        string profilePath = Path.Combine(dir, "profile.json");
        File.WriteAllText(profilePath, """{"ProfileId":"test","Pages":[{"PageName":"A"}]}""");
        return profilePath;
    }

    private static string CreateTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wmd-ahk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDirs(params string[] dirs)
    {
        foreach (string dir in dirs)
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { /* best-effort: FileLog may still hold the stream */ }
        }
    }

    private static (List<(string Interpreter, string Script)> Spawns, AhkLaunchApi Api) Recorder()
    {
        var spawns = new List<(string Interpreter, string Script)>();
        var api = new AhkLaunchApi((interpreter, script) =>
        {
            spawns.Add((interpreter, script));
            return true;
        });
        return (spawns, api);
    }

    /// <summary>Reads the log file with FileShare.ReadWrite (FileLog holds
    /// its own handle while the path is assigned).</summary>
    private static string ReadLog(string logPath)
    {
        if (!File.Exists(logPath)) return "";
        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
