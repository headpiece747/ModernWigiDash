using System.IO;
using Microsoft.Extensions.Time.Testing;
using ModernWigiDash.App;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Core.Plugins;
using ModernWigiDash.Sdk;
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
        // The path is a directory, so the tmp write throws IOException.
        string blocked = Path.Combine(dir, "blocked");
        Directory.CreateDirectory(blocked);
        var log = new List<string>();
        var failing = new ProfilePersistence(blocked, static () => SampleProfile(), log: log.Add);

        failing.Save();

        Assert.AreEqual(1, log.Count);
        StringAssert.Contains(log[0], "Profile save failed");
    }
}
