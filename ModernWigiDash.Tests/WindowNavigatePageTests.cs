using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ModernWigiDash.App.Hotkey;
using ModernWigiDash.App.Power;

namespace ModernWigiDash.Tests;

/// <summary>
/// The window's page navigation and global-hotkey wiring pinned on a live
/// STA window: NavigatePage moves the active page through the
/// SetActivePageIndex gate (an out-of-range step is a no-op, never a wrap),
/// and a WM_HOTKEY posted to the window's HWND routes through the message
/// loop, the window's hook, and the manager to the owning widget's fire path
/// (the profile's page flips). The hotkey API is the injected fake, so the
/// pin never depends on the OS granting (or another program holding) the
/// chord.
/// </summary>
[TestClass]
public class WindowNavigatePageTests
{
    private const uint WmHotkey = 0x0312;

    private static readonly StaHost Host = new("WindowNavigatePage-STA");

    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }

    [TestMethod]
    public void NavigatePage_MovesTheActivePage_AndClampsAtTheBoundary()
    {
        string profilePath = SeedProfile("""{"ProfileId":"test","Pages":[{"PageName":"A"},{"PageName":"B"},{"PageName":"C"}]}""");
        Host.Run<object?>(() =>
        {
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface());
            try
            {
                window.NavigatePage(1);
                Assert.AreEqual(1, window.Profile.ActivePageIndex, "a forward step moves the active page");

                window.NavigatePage(1);
                Assert.AreEqual(2, window.Profile.ActivePageIndex);

                window.NavigatePage(100);
                Assert.AreEqual(2, window.Profile.ActivePageIndex, "past the last page is a no-op (the SetActivePageIndex gate), never a wrap");

                window.NavigatePage(-5);
                Assert.AreEqual(2, window.Profile.ActivePageIndex, "below the first page is a no-op too");

                window.NavigatePage(0);
                Assert.AreEqual(2, window.Profile.ActivePageIndex, "a zero delta is a no-op");
            }
            finally
            {
                window.QuitClose();
            }
            return null;
        });
    }

    [TestMethod]
    public void WmHotkey_RoutesThroughTheHook_ToTheOwningWidgetFirePath()
    {
        string profilePath = SeedProfile("""
        {
            "ProfileId": "test",
            "Pages": [
                { "PageName": "A", "Widgets": [
                    { "PluginId": "hotkey_button", "X": 0, "Y": 0, "Width": 100, "Height": 100,
                      "PropertyValues": { "GlobalHotkey": "Ctrl+Alt+Shift+F9", "ActionType": "Next Page" } }
                ] },
                { "PageName": "B" }
            ]
        }
        """);
        var fake = new FakeHotkeyApi();
        Host.Run<object?>(() =>
        {
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), null, fake.Api);
            try
            {
                window.Show();
                Assert.AreEqual(0, window.Profile.ActivePageIndex);

                // The first registration pass ran at SourceInitialized (the
                // fake recorded the chord with MOD_NOREPEAT at the OS
                // boundary).
                var registration = fake.Registered.Single();
                Assert.AreEqual(
                    GlobalHotkeyChordPolicy.ModAlt | GlobalHotkeyChordPolicy.ModControl |
                    GlobalHotkeyChordPolicy.ModShift | 0x4000,
                    registration.Mod,
                    "the chord's modifiers + Win32 MOD_NOREPEAT (0x4000) ride the registration");
                Assert.AreEqual((ushort)0x78, registration.Vk, "F9's virtual-key code");

                // The OS delivers WM_HOTKEY with the id as wParam. Post the
                // real message to the window's HWND and pump it: the
                // window's HwndSource hook routes it to the widget's fire
                // path, which flips the page synchronously (the fire path's
                // zero-timeout gate completes inline).
                IntPtr handle = new WindowInteropHelper(window).Handle;
                PostMessage(handle, WmHotkey, new IntPtr(registration.Id), IntPtr.Zero);
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(
                    DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);

                Assert.AreEqual(1, window.Profile.ActivePageIndex,
                    "the widget's Next Page action flipped the profile's active page");
            }
            finally
            {
                window.QuitClose();
            }
            return null;
        });
    }

    [TestMethod]
    public void RefreshGlobalHotkeys_DuplicateChord_TheFirstWidgetInProfileOrderWins()
    {
        string profilePath = SeedProfile("""
        {
            "ProfileId": "test",
            "Pages": [
                { "PageName": "A", "Widgets": [
                    { "PluginId": "hotkey_button", "X": 0, "Y": 0, "Width": 100, "Height": 100,
                      "PropertyValues": { "GlobalHotkey": "Ctrl+Alt+Shift+F9", "ActionType": "Next Page" } },
                    { "PluginId": "hotkey_button", "X": 110, "Y": 0, "Width": 100, "Height": 100,
                      "PropertyValues": { "GlobalHotkey": "Ctrl+Alt+Shift+F9", "ActionType": "Previous Page" } }
                ] }
            ]
        }
        """);
        string logPath = Path.Combine(Path.GetTempPath(), $"wmd-hotkey-{Guid.NewGuid():N}", "display_device.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        string originalLogPath = FileLog.LogPath;
        var fake = new FakeHotkeyApi();
        Host.Run<object?>(() =>
        {
            FileLog.LogPath = logPath;
            var window = new MainWindow(new StubPresentMonNative(), profilePath, new NoopPowerModeSource(), new FakeTraySurface(), null, fake.Api);
            try
            {
                window.Show();
                Assert.AreEqual(1, fake.Registered.Count,
                    "the duplicate cell registers once: the first widget in profile order wins");

                FileLog.Flush();
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                StringAssert.Contains(
                    reader.ReadToEnd(),
                    "[HOTKEY] Global hotkey Ctrl+Alt+Shift+F9 is claimed by an earlier widget; the later one stays tap-only",
                    "the later duplicate logs one line");
            }
            finally
            {
                FileLog.LogPath = originalLogPath;
                window.QuitClose();
                try { Directory.Delete(Path.GetDirectoryName(logPath)!, true); }
                catch (IOException) { /* best-effort: FileLog may still hold the stream */ }
            }
            return null;
        });
    }

    /// <summary>Seeds a temp profile the window's production load picks up.</summary>
    private static string SeedProfile(string json)
    {
        string dir = Path.Combine(Path.GetTempPath(), "wmd-navigate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string profilePath = Path.Combine(dir, "profile.json");
        File.WriteAllText(profilePath, json);
        return profilePath;
    }

    /// <summary>The window's fake hotkey API: records every registration and
    /// unregistration, never refuses (a test chord is always free).</summary>
    private sealed class FakeHotkeyApi
    {
        public record Registration(IntPtr Handle, int Id, int Mod, ushort Vk);

        public List<Registration> Registered { get; } = [];
        public List<(IntPtr Handle, int Id, ushort Vk)> Unregistered { get; } = [];

        public HotkeyApi Api { get; }

        public FakeHotkeyApi()
        {
            Api = new HotkeyApi(
                (handle, id, mod, vk) =>
                {
                    Registered.Add(new Registration(handle, id, mod, vk));
                    return true;
                },
                (handle, id, vk) => Unregistered.Add((handle, id, vk)));
        }
    }

    /// <summary>Posts a message to the window's HWND (the OS's WM_HOTKEY
    /// delivery); the message acquires no handle.</summary>
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
