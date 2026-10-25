using System.IO;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Parsing;

namespace ModernWigiDash.Tests;

/// <summary>
/// The library discovery owner's rules: the registry anchor probe (never
/// throws), the multi-volume libraryfolders.vdf parse with the single-root
/// fallback, and the ACF enumeration + parse over a fixture library tree
/// (field extraction, the corrupt-ACF skip, the app-id filename fallback).
/// </summary>
[TestClass]
public sealed class SteamLibraryLocatorTests
{
    private sealed class LibraryFixture : IDisposable
    {
        private readonly string _root;

        public LibraryFixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "wmd-steam-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "steamapps"));
        }

        public string Root => _root;

        public string SteamAppsDir => Path.Combine(_root, "steamapps");

        public void WriteAcf(string appId, string content)
            => File.WriteAllText(Path.Combine(SteamAppsDir, $"appmanifest_{appId}.acf"), content);

        public void WriteLibraryFolders(string content)
            => File.WriteAllText(Path.Combine(SteamAppsDir, "libraryfolders.vdf"), content);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* a fixture file is still open on a hostile platform; the temp sweep takes it. */ }
            catch (UnauthorizedAccessException) { /* same: the ACL denied the delete; the temp sweep takes it. */ }
        }
    }

    private const string ValidAcf = """
        "AppState"
        {
            "appid"                 "730"
            "Universe"              "1"
            "name"                  "Counter-Strike 2"
            "StateFlags"            "4"
            "installdir"            "Counter-Strike Global Offensive"
            "LastPlayed"            "1700000000"
            "BytesDownloaded"       "0"
            "BytesToDownload"       "0"
            "hours_on_record"       "512.5"
        }
        """;

    private const string UpdatingAcf = """
        "AppState"
        {
            "appid"                 "1245620"
            "name"                  "Star Wars Jedi Survivor"
            "StateFlags"            "1048576"
            "installdir"            "Star Wars Jedi Survivor"
            "BytesDownloaded"       "100"
            "BytesToDownload"       "300"
        }
        """;

    /// <summary>An ACF in the shape current Steam actually writes: no
    /// <c>executable</c> and no <c>hours_on_record</c>, so both facts must come
    /// from the install folder and localconfig.vdf.</summary>
    private const string RealShapeAcf = """
        "AppState"
        {
            "appid"                 "730"
            "name"                  "Counter-Strike 2"
            "StateFlags"            "4"
            "installdir"            "cs2"
            "LastPlayed"            "1700000000"
            "BytesDownloaded"       "0"
            "BytesToDownload"       "0"
        }
        """;

    /// <summary>Writes an <c>.exe</c> of <paramref name="size"/> bytes (the
    /// resolver ranks by size, so the contents do not matter).</summary>
    private static void WriteBinary(string directory, string name, int size)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name), new byte[size]);
    }

    [TestMethod]
    public void TryFindSteamPath_NeverThrows()
    {
        // The probe is a graceful read: a machine with (or without) Steam
        // yields a path (or null), never an exception into the caller.
        string? path = SteamLibraryLocator.TryFindSteamPath();
        if (path is not null)
            Assert.IsTrue(Directory.Exists(path));
    }

    [TestMethod]
    public void GetLibraryRoots_MissingVdf_FallsBackToTheSingleRoot()
    {
        using var fixture = new LibraryFixture();

        IReadOnlyList<string> roots = SteamLibraryLocator.GetLibraryRoots(fixture.Root);

        CollectionAssert.AreEquivalent(new[] { fixture.Root }, new List<string>(roots));
    }

    [TestMethod]
    public void GetLibraryRoots_MultiVolumeVdf_ReturnsEveryMountedPath()
    {
        using var fixture = new LibraryFixture();
        fixture.WriteLibraryFolders("""
            "libraryfolders"
            {
                "0"
                {
                    "path"    "C:\\Steam"
                    "label"   "main"
                }
                "1"
                {
                    "path"    "D:\\SteamLibrary"
                    "label"   "games"
                }
            }
            """);

        IReadOnlyList<string> roots = SteamLibraryLocator.GetLibraryRoots(fixture.Root);

        CollectionAssert.AreEquivalent(new[] { @"C:\Steam", @"D:\SteamLibrary" }, new List<string>(roots));
    }

    [TestMethod]
    public void GetLibraryRoots_UnreadableVdf_FallsBackToTheSingleRoot()
    {
        using var fixture = new LibraryFixture();
        fixture.WriteLibraryFolders("not a vdf file at all");

        // A VDF with no "path" keys is the same as a missing one: the
        // single-root fallback.
        IReadOnlyList<string> roots = SteamLibraryLocator.GetLibraryRoots(fixture.Root);

        CollectionAssert.AreEquivalent(new[] { fixture.Root }, new List<string>(roots));
    }

    [TestMethod]
    public void LoadManifests_ValidAcfs_ParsesEveryField()
    {
        using var fixture = new LibraryFixture();
        fixture.WriteAcf("730", ValidAcf);
        fixture.WriteAcf("1245620", UpdatingAcf);

        IReadOnlyList<SteamAppManifest> manifests = SteamLibraryLocator.LoadManifests([fixture.Root]);

        Assert.AreEqual(2, manifests.Count);
        SteamAppManifest cs2 = manifests.First(m => m.AppId == 730u);
        Assert.AreEqual("Counter-Strike 2", cs2.Name);
        Assert.AreEqual("Counter-Strike Global Offensive", cs2.InstallDir);
        Assert.IsTrue(cs2.IsInstalled);
        Assert.IsFalse(cs2.IsUpdating);
        Assert.AreEqual(1.0f, cs2.DownloadProgress, 1e-3);
        Assert.AreEqual(512.5, cs2.HoursOnRecord, 1e-9);
        Assert.IsNotNull(cs2.LastPlayedUtc);

        SteamAppManifest jedi = manifests.First(m => m.AppId == 1245620u);
        Assert.IsTrue(jedi.IsUpdating);
        Assert.AreEqual(0.25f, jedi.DownloadProgress, 1e-3);
    }

    [TestMethod]
    public void LoadManifests_CorruptAcf_SkipsItAndLogsOneLine()
    {
        using var fixture = new LibraryFixture();
        fixture.WriteAcf("730", ValidAcf);
        // ACF with no appid key AND a non-numeric filename tail: the app id
        // is unrecoverable, so the file is unparseable (the truncation in an
        // optional field alone would still parse with defaults).
        File.WriteAllText(Path.Combine(fixture.SteamAppsDir, "appmanifest_corrupt.acf"), "\"AppState\" { \"name\" \"trunc");

        var logLines = new List<string>();
        IReadOnlyList<SteamAppManifest> manifests = SteamLibraryLocator.LoadManifests([fixture.Root], logLines.Add);

        Assert.AreEqual(1, manifests.Count);
        Assert.AreEqual(730u, manifests[0].AppId);
        Assert.AreEqual(1, logLines.Count);
        Assert.IsTrue(logLines[0].Contains("appmanifest_corrupt.acf", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LoadManifests_MissingVolume_SkipsSilently()
    {
        string absent = Path.Combine(Path.GetTempPath(), "wmd-steam-absent-" + Guid.NewGuid().ToString("N"));

        IReadOnlyList<SteamAppManifest> manifests = SteamLibraryLocator.LoadManifests([absent]);

        Assert.AreEqual(0, manifests.Count);
    }

    [TestMethod]
    public void LoadManifests_DuplicateAppIdAcrossVolumes_KeepsOneManifest()
    {
        using var first = new LibraryFixture();
        using var second = new LibraryFixture();
        first.WriteAcf("730", ValidAcf);
        second.WriteAcf("730", ValidAcf);

        IReadOnlyList<SteamAppManifest> manifests = SteamLibraryLocator.LoadManifests([first.Root, second.Root]);

        Assert.AreEqual(1, manifests.Count);
    }

    [TestMethod]
    public void LoadManifests_AcfWithoutExecutable_ResolvesTheLargestGameBinary()
    {
        using var fixture = new LibraryFixture();
        fixture.WriteAcf("730", RealShapeAcf);
        string install = Path.Combine(fixture.SteamAppsDir, "common", "cs2");
        WriteBinary(install, "vcredist_x64.exe", 4096);        // a helper, and the largest file
        WriteBinary(install, "UnityCrashHandler64.exe", 2048); // a helper
        WriteBinary(install, "cs2.exe", 1024);                 // the game itself

        IReadOnlyList<SteamAppManifest> manifests = SteamLibraryLocator.LoadManifests([fixture.Root]);

        Assert.AreEqual(
            "cs2",
            manifests[0].ProcessName,
            "the ACF carries no executable, so the largest non-helper binary is the game (helpers never become the process key)");
    }

    [TestMethod]
    public void LoadManifests_ExecutableInANestedBinariesFolder_IsFound()
    {
        // The Unreal shape: the shipping binary sits under Binaries\Win64,
        // below the launcher a top-level-only scan would pick.
        using var fixture = new LibraryFixture();
        fixture.WriteAcf("1245620", UpdatingAcf);
        string install = Path.Combine(fixture.SteamAppsDir, "common", "Star Wars Jedi Survivor");
        WriteBinary(install, "launcher.exe", 1024);
        WriteBinary(Path.Combine(install, "SwGame", "Binaries", "Win64"), "Jedi-Win64-Shipping.exe", 8192);

        IReadOnlyList<SteamAppManifest> manifests = SteamLibraryLocator.LoadManifests([fixture.Root]);

        Assert.AreEqual("Jedi-Win64-Shipping", manifests[0].ProcessName);
    }

    [TestMethod]
    public void LoadManifests_LocalConfigPlaytime_FillsHoursOnRecord()
    {
        using var fixture = new LibraryFixture();
        fixture.WriteAcf("730", RealShapeAcf);
        string config = Path.Combine(fixture.Root, "userdata", "17013261", "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "localconfig.vdf"), """
            "UserLocalConfigStore"
            {
                "Software"
                {
                    "Valve"
                    {
                        "Steam"
                        {
                            "apps"
                            {
                                "730"
                                {
                                    "LastPlayed"    "1700000000"
                                    "Playtime"      "450"
                                }
                                "570"
                                {
                                    "cloud"
                                    {
                                        "last_sync_state"   "synchronized"
                                    }
                                }
                            }
                        }
                    }
                }
            }
            """);

        IReadOnlyList<SteamAppManifest> manifests = SteamLibraryLocator.LoadManifests([fixture.Root], null, fixture.Root);

        Assert.AreEqual(7.5, manifests[0].HoursOnRecord, 1e-9, "450 minutes of localconfig playtime is 7.5 hours");
    }

    [TestMethod]
    public void ParsePlaytimes_NestedAppsBlock_ReadsMinutesPerApp()
    {
        Dictionary<uint, double> playtimes = SteamLibraryLocator.ParsePlaytimes("""
            "UserLocalConfigStore"
            {
                "Software" { "Valve" { "Steam" { "apps"
                {
                    "730" { "Playtime" "60" }
                    "570" { "Playtime" "0" }
                    "10"  { "Playtime" "not-a-number" }
                    "not-an-app" { "Playtime" "90" }
                } } } }
            }
            """);

        Assert.AreEqual(1.0, playtimes[730u], 1e-9);
        Assert.IsFalse(playtimes.ContainsKey(570u), "a zero playtime carries no fact");
        Assert.IsFalse(playtimes.ContainsKey(10u), "a non-numeric playtime is skipped, never thrown");
        Assert.AreEqual(1, playtimes.Count);
    }

    [TestMethod]
    public void TryParseManifest_AppIdKeyMissing_RecoversFromTheFileName()
    {
        string acf = """
            "AppState"
            {
                "name" "Dota 2"
                "StateFlags" "4"
            }
            """;

        Assert.IsTrue(SteamLibraryLocator.TryParseManifest(acf, "appmanifest_570.acf", out SteamAppManifest? manifest));
        Assert.AreEqual(570u, manifest!.AppId);
        Assert.AreEqual("Dota 2", manifest.Name);
    }

    [TestMethod]
    public void TryParseManifest_NoAppIdAnywhere_ReturnsFalse()
    {
        // A numeric filename tail recovers the app id (the documented
        // filename fallback) — so "no app id anywhere" needs both a content
        // without the key and a filename without digits.
        Assert.IsTrue(SteamLibraryLocator.TryParseManifest("\"AppState\" {}", "appmanifest_570.acf", out SteamAppManifest? recovered));
        Assert.AreEqual(570u, recovered!.AppId);

        Assert.IsFalse(SteamLibraryLocator.TryParseManifest("\"AppState\" { \"name\" \"x\" }", "not-an-acf-name.txt", out _));
    }
}
