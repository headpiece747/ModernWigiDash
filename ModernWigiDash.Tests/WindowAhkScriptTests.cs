using System.IO;
using ModernWigiDash.App.Hotkey;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

/// <summary>
/// The WINDOW's half of the AutoHotkey spawn path (ADR-0019): that the window's
/// launch entry point reaches <see cref="AhkSpawnPolicy"/> and hands it the
/// script path, so a window that stopped calling the policy would fail here.
///
/// The policy's own behaviour — the four refusals, the launch-failure line and
/// the success path — moved to <see cref="AhkSpawnPolicyTests"/>, which drives
/// the module directly. Those five cases used to run through a live window here,
/// paying an XAML tree, a profile load, a telemetry session, a tray icon, a temp
/// settings store, a temp profile and a swapped FileLog path per test, to assert
/// a spawn count and one log line that the policy already owned. One window
/// remains, because the wiring is the one thing only the window can show
/// (the architecture review's "wire nothing" seam candidate, 2026-09-27).
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
    public void LaunchAutoHotkeyScript_RoutesThroughThePolicyToTheSpawnSeam()
    {
        var spawns = new List<(string Interpreter, string Script)>();
        var ahkApi = new AhkLaunchApi((interpreter, script) =>
        {
            spawns.Add((interpreter, script));
            return true;
        });
        string settingsDir = CreateTempDir();
        // The interpreter path must exist for the policy to reach the spawn seam;
        // the dummy file is never executed (the seam records instead).
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

                // The wiring claim, and only that: the window reached the policy,
                // the policy reached the seam, and the script path survived the
                // hop. Every refusal and the success line are the policy's, pinned
                // without a window in AhkSpawnPolicyTests.
                Assert.AreEqual(1, spawns.Count, "the window's launch entry point must reach the policy's spawn seam");
                Assert.AreEqual(interpreter, spawns[0].Interpreter, "the interpreter is the user's settings path");
                Assert.AreEqual(ScriptPath, spawns[0].Script, "the script path is the action's command value, carried through the window unchanged");
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
