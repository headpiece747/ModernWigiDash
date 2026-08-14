using System.IO;
using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.App;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.Tests;

[TestClass]
public sealed class ProfilePersistenceTests
{
    private static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wmd-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ProfileLayout SampleProfile()
        => new()
        {
            Pages = [new PageLayout { PageName = "P1" }]
        };

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time");
            await Task.Delay(10);
        }
    }

    [TestMethod]
    public void DefaultProfilePath_ReturnsLocalAppDataModernWigiDashProfileJson()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernWigiDash", "profile.json");

        Assert.AreEqual(expected, ProfilePersistence.DefaultProfilePath());
    }

    [TestMethod]
    public void Load_AbsentFile_ReturnsNull()
    {
        var persistence = new ProfilePersistence(
            Path.Combine(NewTempDir(), "profile.json"),
            static () => SampleProfile());

        Assert.IsNull(persistence.Load(new WidgetPluginLoader(), new TestContext()));
    }

    [TestMethod]
    public void Load_ValidFile_ReturnsRehydratedProfile()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(TextLabelWidget));
        var context = new TestContext();

        var source = new ProfileLayout { Pages = [new PageLayout { PageName = "P1" }] };
        ProfileOps.PlaceWidget(source, loader, context, "text_label", 0, 0);
        File.WriteAllText(path, ProfileOps.ExportJson(source));

        var persistence = new ProfilePersistence(path, static () => SampleProfile());
        var loaded = persistence.Load(loader, context);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.Pages.Count);
        Assert.AreEqual("P1", loaded.Pages[0].PageName);
        Assert.AreEqual(1, loaded.Pages[0].Widgets.Count);
        Assert.IsNotNull(loaded.Pages[0].Widgets[0].ActiveInstance);
    }

    [TestMethod]
    public void Load_CorruptJson_ReturnsNull()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        File.WriteAllText(path, "{ not valid json");

        var persistence = new ProfilePersistence(path, static () => SampleProfile());

        Assert.IsNull(persistence.Load(new WidgetPluginLoader(), new TestContext()));
    }

    [TestMethod]
    public void Load_TrustedFile_PreservesUserConfiguredValues()
    {
        // C1 regression: the app's own profile must load WITHOUT the
        // untrusted-import sanitizer, which would wipe the user's configured
        // Hotkey ActionCommand, absolute Picture ImagePath, and page
        // BackgroundImagePath at every restart.
        string path = Path.Combine(NewTempDir(), "profile.json");
        var loader = new WidgetPluginLoader();
        loader.RegisterBuiltInPlugin(typeof(HotkeyButtonWidget));
        loader.RegisterBuiltInPlugin(typeof(PictureAndGifWidget));
        var context = new TestContext();

        var source = new ProfileLayout
        {
            Pages =
            [
                new PageLayout
                {
                    PageName = "P1",
                    BackgroundImagePath = @"C:\Wallpapers\page-bg.png",
                    Widgets =
                    [
                        new PlacedWidgetInstance
                        {
                            PluginId = "hotkey_button",
                            X = 0, Y = 0, Width = 203, Height = 148,
                            PropertyValues = new Dictionary<string, object?> { ["ActionCommand"] = @"C:\Tools\launch.bat" }
                        },
                        new PlacedWidgetInstance
                        {
                            PluginId = "picture_viewer",
                            X = 203, Y = 0, Width = 406, Height = 296,
                            PropertyValues = new Dictionary<string, object?> { ["ImagePath"] = @"C:\Pictures\Wallpapers" }
                        }
                    ]
                }
            ]
        };
        File.WriteAllText(path, ProfileOps.ExportJson(source));

        var persistence = new ProfilePersistence(path, static () => SampleProfile());
        var loaded = persistence.Load(loader, context);

        Assert.IsNotNull(loaded);
        Assert.AreEqual(@"C:\Wallpapers\page-bg.png", loaded.Pages[0].BackgroundImagePath);
        var hotkey = loaded.Pages[0].Widgets[0];
        // PropertyValues deserialize as JsonElement; ToString() unwraps the
        // string. The C1 point is that the value SURVIVES untrimmed — a wiped
        // value would be "" here, never a JsonElement holding the full path.
        Assert.AreEqual(@"C:\Tools\launch.bat", hotkey.PropertyValues["ActionCommand"]?.ToString());
        var picture = loaded.Pages[0].Widgets[1];
        Assert.AreEqual(@"C:\Pictures\Wallpapers", picture.PropertyValues["ImagePath"]?.ToString());
    }

    [TestMethod]
    public void Load_OversizedFile_ReturnsNull()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        File.WriteAllText(path, new string('x', 10 * 1024 * 1024 + 1));

        var persistence = new ProfilePersistence(path, static () => SampleProfile());

        Assert.IsNull(persistence.Load(new WidgetPluginLoader(), new TestContext()));
    }

    [TestMethod]
    public void Save_WritesExportJsonAtomically_NoTempFileLeft()
    {
        string dir = NewTempDir();
        string path = Path.Combine(dir, "profile.json");
        var persistence = new ProfilePersistence(path, static () => SampleProfile());

        persistence.Save();

        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(File.Exists(path + ".tmp"));
        var loaded = System.Text.Json.JsonSerializer.Deserialize<ProfileLayout>(File.ReadAllText(path));
        Assert.IsNotNull(loaded);
        Assert.AreEqual(1, loaded.Pages.Count);
    }

    [TestMethod]
    public void Save_CreatesDirectory_WhenMissing()
    {
        string dir = Path.Combine(NewTempDir(), "nested", "dir");
        string path = Path.Combine(dir, "profile.json");
        var persistence = new ProfilePersistence(path, static () => SampleProfile());

        persistence.Save();

        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public void Save_WriteFailure_IsLogged_NotThrown()
    {
        string dir = NewTempDir();
        // The profile path is a directory: the tmp write succeeds (blocked.tmp
        // is a sibling file), but the File.Move onto the directory path throws
        // IOException — Save must log it and leave no .tmp litter behind.
        string blocked = Path.Combine(dir, "blocked");
        Directory.CreateDirectory(blocked);
        var log = new List<string>();
        var failing = new ProfilePersistence(blocked, static () => SampleProfile(), log: log.Add);

        failing.Save();

        Assert.AreEqual(1, log.Count);
        StringAssert.Contains(log[0], "Profile save failed");
        Assert.IsFalse(File.Exists(blocked + ".tmp"));
    }

    [TestMethod]
    public async Task MarkDirty_NoSaveBeforeDebounce_CoalescesRepeatedMutations()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        var time = new FakeTimeProvider();
        int saves = 0;
        var persistence = new ProfilePersistence(
            path,
            () => { saves++; return SampleProfile(); },
            debounceDelay: TimeSpan.FromSeconds(2),
            timeProvider: time);

        persistence.MarkDirty();
        persistence.MarkDirty();
        persistence.MarkDirty();
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.AreEqual(0, saves, "no save before the debounce elapses");
        Assert.IsFalse(File.Exists(path));

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => saves == 1, timeoutMs: 2000);

        Assert.AreEqual(1, saves, "three mutations within the window coalesce to one save");
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task MarkDirty_SecondMutationAfterSave_ArmsAnotherSave()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        var time = new FakeTimeProvider();
        int saves = 0;
        var persistence = new ProfilePersistence(
            path,
            () => { saves++; return SampleProfile(); },
            debounceDelay: TimeSpan.FromSeconds(2),
            timeProvider: time);

        persistence.MarkDirty();
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => saves == 1, timeoutMs: 2000);

        persistence.MarkDirty();
        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => saves == 2, timeoutMs: 2000);

        Assert.AreEqual(2, saves);
    }

    [TestMethod]
    public void Flush_SavesImmediately_EvenWithinDebounceWindow()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        var time = new FakeTimeProvider();
        int saves = 0;
        var persistence = new ProfilePersistence(
            path,
            () => { saves++; return SampleProfile(); },
            debounceDelay: TimeSpan.FromSeconds(2),
            timeProvider: time);

        persistence.MarkDirty();
        persistence.Flush();

        Assert.AreEqual(1, saves);
        Assert.IsTrue(File.Exists(path));

        // The pending debounce must not fire a second save.
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, saves);
    }

    [TestMethod]
    public async Task MarkDirty_AfterDispose_DoesNotSave()
    {
        string path = Path.Combine(NewTempDir(), "profile.json");
        var time = new FakeTimeProvider();
        int saves = 0;
        var persistence = new ProfilePersistence(
            path,
            () => { saves++; return SampleProfile(); },
            debounceDelay: TimeSpan.FromSeconds(2),
            timeProvider: time);

        persistence.Dispose();
        persistence.MarkDirty();
        time.Advance(TimeSpan.FromSeconds(3));

        Assert.AreEqual(0, saves);
        Assert.IsFalse(File.Exists(path));
    }
}
