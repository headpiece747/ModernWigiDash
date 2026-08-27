using System.IO;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

[TestClass]
public class ThemeSettingsTests
{
    [TestMethod]
    public void ParseColor_NullOrWhitespace_ReturnsNull()
    {
        Assert.IsNull(ThemeSettings.ParseColor(null));
        Assert.IsNull(ThemeSettings.ParseColor(""));
        Assert.IsNull(ThemeSettings.ParseColor("   "));
    }

    [TestMethod]
    public void ParseColor_6DigitHex_ReturnsOpaqueColor()
    {
        RgbaColor? color = ThemeSettings.ParseColor("#F59E0B");

        Assert.IsNotNull(color);
        Assert.AreEqual(255, color.Value.A);
        Assert.AreEqual(0xF5, color.Value.R);
        Assert.AreEqual(0x9E, color.Value.G);
        Assert.AreEqual(0x0B, color.Value.B);
    }

    [TestMethod]
    public void ParseColor_8DigitHex_ReturnsColorWithAlpha()
    {
        RgbaColor? color = ThemeSettings.ParseColor("#80FF0000");

        Assert.IsNotNull(color);
        Assert.AreEqual(0x80, color.Value.A);
        Assert.AreEqual(0xFF, color.Value.R);
        Assert.AreEqual(0x00, color.Value.G);
        Assert.AreEqual(0x00, color.Value.B);
    }

    [TestMethod]
    public void ParseColor_LeadingHashOptional()
    {
        RgbaColor? color = ThemeSettings.ParseColor("10B981");

        Assert.IsNotNull(color);
        Assert.AreEqual(255, color.Value.A);
        Assert.AreEqual(0x10, color.Value.R);
        Assert.AreEqual(0xB9, color.Value.G);
        Assert.AreEqual(0x81, color.Value.B);
    }

    [TestMethod]
    public void ParseColor_InvalidLength_ReturnsNull()
    {
        Assert.IsNull(ThemeSettings.ParseColor("#FFF"));
        Assert.IsNull(ThemeSettings.ParseColor("#11223"));
        Assert.IsNull(ThemeSettings.ParseColor("#1122334"));
        Assert.IsNull(ThemeSettings.ParseColor("#1122334455"));
    }

    [TestMethod]
    public void ParseColor_InvalidHexDigits_ReturnsNull()
    {
        Assert.IsNull(ThemeSettings.ParseColor("#GGGGGG"));
        Assert.IsNull(ThemeSettings.ParseColor("#12ZZ34"));
    }

    [TestMethod]
    public void ParseColor_TrimsSurroundingWhitespace()
    {
        RgbaColor? color = ThemeSettings.ParseColor("  #FAFAFA  ");

        Assert.IsNotNull(color);
        Assert.AreEqual(255, color.Value.A);
        Assert.AreEqual(0xFA, color.Value.R);
        Assert.AreEqual(0xFA, color.Value.G);
        Assert.AreEqual(0xFA, color.Value.B);
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesAllProperties()
    {
        var theme = new ThemeSettings
        {
            BgDark = "#000000",
            BgPanel = "#111111",
            BgCard = "#222222",
            Border = "#333333",
            AccentRed = "#444444",
            M3Primary = "#555555",
            M3PrimaryContainer = "#666666",
            M3OnPrimaryContainer = "#777777",
            AccentGreen = "#888888",
            TextPrimary = "#999999",
            TextSecondary = "#AAAAAA",
            ControlHover = "#BBBBBB",
            DropdownHover = "#CCCCCC",
            TitleBar = "#DDDDDD",
            StatusBarBackground = "#EEEEEE",
            DangerBackground = "#FF0000",
            DangerBorder = "#FF1111",
            SuccessBackground = "#00FF00",
            SuccessBorder = "#00FFFF"
        };

        string json = System.Text.Json.JsonSerializer.Serialize(theme);
        var clone = System.Text.Json.JsonSerializer.Deserialize<ThemeSettings>(json);

        Assert.IsNotNull(clone);
        Assert.AreEqual(theme.BgDark, clone.BgDark);
        Assert.AreEqual(theme.BgPanel, clone.BgPanel);
        Assert.AreEqual(theme.BgCard, clone.BgCard);
        Assert.AreEqual(theme.Border, clone.Border);
        Assert.AreEqual(theme.AccentRed, clone.AccentRed);
        Assert.AreEqual(theme.M3Primary, clone.M3Primary);
        Assert.AreEqual(theme.M3PrimaryContainer, clone.M3PrimaryContainer);
        Assert.AreEqual(theme.M3OnPrimaryContainer, clone.M3OnPrimaryContainer);
        Assert.AreEqual(theme.AccentGreen, clone.AccentGreen);
        Assert.AreEqual(theme.TextPrimary, clone.TextPrimary);
        Assert.AreEqual(theme.TextSecondary, clone.TextSecondary);
        Assert.AreEqual(theme.ControlHover, clone.ControlHover);
        Assert.AreEqual(theme.DropdownHover, clone.DropdownHover);
        Assert.AreEqual(theme.TitleBar, clone.TitleBar);
        Assert.AreEqual(theme.StatusBarBackground, clone.StatusBarBackground);
        Assert.AreEqual(theme.DangerBackground, clone.DangerBackground);
        Assert.AreEqual(theme.DangerBorder, clone.DangerBorder);
        Assert.AreEqual(theme.SuccessBackground, clone.SuccessBackground);
        Assert.AreEqual(theme.SuccessBorder, clone.SuccessBorder);
    }

    [TestMethod]
    public void Defaults_AreValidHexColors()
    {
        var theme = new ThemeSettings();

        foreach (string value in new[]
                 {
                     theme.BgDark, theme.BgPanel, theme.BgCard, theme.Border,
                     theme.AccentRed, theme.M3Primary, theme.M3PrimaryContainer,
                     theme.M3OnPrimaryContainer, theme.AccentGreen,
                     theme.TextPrimary, theme.TextSecondary,
                     theme.ControlHover, theme.DropdownHover, theme.TitleBar,
                     theme.StatusBarBackground, theme.DangerBackground,
                     theme.DangerBorder, theme.SuccessBackground, theme.SuccessBorder
                 })
        {
            Assert.IsNotNull(ThemeSettings.ParseColor(value), $"'{value}' should parse");
        }
    }

    [TestMethod]
    public void FriendlyName_ReturnsKnownLabelAndFallsBackToRawName()
    {
        Assert.AreEqual("Card / Input Background", ThemePresentation.FriendlyName("BgCard"));
        Assert.AreEqual("UnknownProp", ThemePresentation.FriendlyName("UnknownProp"));
    }

    [TestMethod]
    public void DisplayNamesAndGroups_CoverEveryThemeProperty()
    {
        var theme = new ThemeSettings();
        var propertyNames = new[]
        {
            nameof(theme.BgDark), nameof(theme.BgPanel), nameof(theme.BgCard),
            nameof(theme.Border), nameof(theme.AccentRed), nameof(theme.M3Primary),
            nameof(theme.M3PrimaryContainer), nameof(theme.M3OnPrimaryContainer),
            nameof(theme.AccentGreen), nameof(theme.TextPrimary),
            nameof(theme.TextSecondary), nameof(theme.ControlHover),
            nameof(theme.DropdownHover), nameof(theme.TitleBar),
            nameof(theme.StatusBarBackground), nameof(theme.DangerBackground),
            nameof(theme.DangerBorder), nameof(theme.SuccessBackground),
            nameof(theme.SuccessBorder)
        };

        foreach (string name in propertyNames)
        {
            Assert.IsTrue(ThemePresentation.DisplayNames.ContainsKey(name), $"DisplayNames missing {name}");
            Assert.IsTrue(ThemePresentation.Descriptions.ContainsKey(name), $"Descriptions missing {name}");
            Assert.IsTrue(ThemePresentation.Groups.ContainsKey(name), $"Groups missing {name}");
        }
    }

    [TestMethod]
    public void ThemeSettings_ParseColor_HandlesRgbAndArgb()
    {
        var rgb = ModernWigiDash.Core.Theming.ThemeSettings.ParseColor("#FFCD85");
        Assert.IsNotNull(rgb);
        Assert.AreEqual(255, rgb.Value.R);
        Assert.AreEqual(205, rgb.Value.G);
        Assert.AreEqual(133, rgb.Value.B);

        var argb = ModernWigiDash.Core.Theming.ThemeSettings.ParseColor("#CCFFCD85");
        Assert.IsNotNull(argb);
        Assert.AreEqual(204, argb.Value.A);

        Assert.IsNull(ModernWigiDash.Core.Theming.ThemeSettings.ParseColor("not-a-color"));
    }

    [TestMethod]
    public void ThemeSettings_DisplayMetadata_CoversEveryColorProperty()
    {
        var props = typeof(ModernWigiDash.Core.Theming.ThemeSettings).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .ToList();

        Assert.IsTrue(props.Count > 0, "ThemeSettings should expose color properties");

        foreach (string name in props.Select(p => p.Name))
        {
            Assert.IsTrue(ThemePresentation.DisplayNames.ContainsKey(name),
                $"Missing friendly display name for '{name}'");
            Assert.IsTrue(ThemePresentation.Descriptions.ContainsKey(name),
                $"Missing description for '{name}'");
            Assert.IsTrue(ThemePresentation.Groups.ContainsKey(name),
                $"Missing group for '{name}'");
        }
    }

    [TestMethod]
    public void StringProperties_ListsEveryColorPropertyInDeclarationOrder()
    {
        string[] names = ThemeSettings.StringProperties.Select(p => p.Name).ToArray();

        CollectionAssert.AreEqual(
            new[]
            {
                nameof(ThemeSettings.BgDark), nameof(ThemeSettings.BgPanel), nameof(ThemeSettings.BgCard),
                nameof(ThemeSettings.Border), nameof(ThemeSettings.AccentRed), nameof(ThemeSettings.M3Primary),
                nameof(ThemeSettings.M3PrimaryContainer), nameof(ThemeSettings.M3OnPrimaryContainer),
                nameof(ThemeSettings.AccentGreen), nameof(ThemeSettings.TextPrimary),
                nameof(ThemeSettings.TextSecondary), nameof(ThemeSettings.ControlHover),
                nameof(ThemeSettings.DropdownHover), nameof(ThemeSettings.TitleBar),
                nameof(ThemeSettings.StatusBarBackground), nameof(ThemeSettings.DangerBackground),
                nameof(ThemeSettings.DangerBorder), nameof(ThemeSettings.SuccessBackground),
                nameof(ThemeSettings.SuccessBorder)
            },
            names);

        Assert.AreEqual(19, names.Length,
            "every themeable color property must be enumerated by StringProperties (the applier, fingerprint, and theme dialog consume it)");
    }

    [TestMethod]
    public void ThemeSettings_DefaultsToTitaniumAmberPalette()
    {
        var theme = new ModernWigiDash.Core.Theming.ThemeSettings();
        Assert.AreEqual("#121214", theme.BgDark);
        Assert.AreEqual("#1A1A1E", theme.BgPanel);
        Assert.AreEqual("#26262B", theme.BgCard);
        Assert.AreEqual("#3F3F46", theme.Border);
        Assert.AreEqual(theme.Border, theme.M3PrimaryContainer, "zinc-700 trio: M3PrimaryContainer follows Border");
        Assert.AreEqual(theme.Border, theme.ControlHover, "zinc-700 trio: ControlHover follows Border");
        Assert.AreEqual("#F59E0B", theme.AccentRed);
        Assert.AreEqual("#FBBF24", theme.M3Primary);
        Assert.AreEqual("#FAFAFA", theme.TextPrimary);
        Assert.AreEqual("#A1A1AA", theme.TextSecondary);
        Assert.AreEqual("#0B0B0C", theme.TitleBar);
    }

    // ── ADR-0021: the theme file lives in the user state dir ──────────

    private static string NewThemeTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wmd-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string WriteThemeFile(string dir, string name, string bgDark)
    {
        string path = Path.Combine(dir, name);
        bool ok = ThemeSettings.Save(new ThemeSettings { BgDark = bgDark }, path);
        Assert.IsTrue(ok, $"the seam save to '{path}' must succeed");
        return path;
    }

    [TestMethod]
    public void DefaultPath_LivesInTheUserStateDir()
    {
        string expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernWigiDash", "app_theme.json");

        Assert.AreEqual(expected, ThemeSettings.DefaultPath(),
            "the one theme file location is the state dir, beside profile.json and app_settings.json");
    }

    [TestMethod]
    public void DefaultPath_LivesInTheProfileStateDirectory()
    {
        string stateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProfilePersistence.DirectoryName);

        Assert.IsTrue(
            ThemeSettings.DefaultPath().StartsWith(stateDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "lockstep pin: the theme file must live in the profile's state dir (one dir for profile, settings, and theme)");
    }

    [TestMethod]
    public void LegacyPath_IsTheExeDirThemeFile()
        => Assert.AreEqual(Path.Combine(AppContext.BaseDirectory, "app_theme.json"), ThemeSettings.LegacyPath());

    [TestMethod]
    public void Save_CreatesTheMissingDirectory_AndRoundTrips()
    {
        string dir = NewThemeTempDir();
        try
        {
            string path = Path.Combine(dir, "nested", "deeper", "app_theme.json");

            Assert.IsTrue(ThemeSettings.Save(new ThemeSettings { BgDark = "#010203" }, path));

            var read = ThemeSettings.LoadFrom(path);
            Assert.IsNotNull(read);
            Assert.AreEqual("#010203", read.BgDark);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void LoadFrom_AbsentOrCorrupt_ReturnsNull()
    {
        string dir = NewThemeTempDir();
        try
        {
            Assert.IsNull(ThemeSettings.LoadFrom(Path.Combine(dir, "absent.json")));

            string corrupt = Path.Combine(dir, "corrupt.json");
            File.WriteAllText(corrupt, "{ not a theme");
            Assert.IsNull(ThemeSettings.LoadFrom(corrupt));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Load_TargetAbsentLegacyParseable_MigratesTheLegacyCopy()
    {
        string dir = NewThemeTempDir();
        var lines = new List<string>();
        try
        {
            string target = Path.Combine(dir, "state", "app_theme.json");
            string legacy = WriteThemeFile(dir, "legacy.json", "#A1A2A3");

            var loaded = ThemeSettings.Load(target, legacy, lines.Add);

            Assert.AreEqual("#A1A2A3", loaded.BgDark, "the migrated colors are what the user last saw");
            var migrated = ThemeSettings.LoadFrom(target);
            Assert.IsNotNull(migrated, "the migration must carry the copy across to the state dir");
            Assert.AreEqual("#A1A2A3", migrated.BgDark);
            Assert.AreEqual(1, lines.Count, "exactly one log line for the migration");
            StringAssert.Contains(lines[0], "Migrated legacy theme file");
            StringAssert.Contains(lines[0], legacy);
            StringAssert.Contains(lines[0], target);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Load_TargetPresent_ReturnsTheTarget_WithoutMigrating()
    {
        string dir = NewThemeTempDir();
        var lines = new List<string>();
        try
        {
            string target = WriteThemeFile(dir, "state.json", "#010203");
            string legacy = WriteThemeFile(dir, "legacy.json", "#A1A2A3");

            var loaded = ThemeSettings.Load(target, legacy, lines.Add);

            Assert.AreEqual("#010203", loaded.BgDark, "the state file wins when present");
            Assert.AreEqual(0, lines.Count, "no migration runs (and nothing is logged) once the state file exists");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Load_TargetAbsentLegacyAbsent_ReturnsTheDefaultsWithNoLogLine()
    {
        string dir = NewThemeTempDir();
        var lines = new List<string>();
        try
        {
            var loaded = ThemeSettings.Load(
                Path.Combine(dir, "state", "app_theme.json"),
                Path.Combine(dir, "absent-legacy.json"),
                lines.Add);

            Assert.AreEqual(new ThemeSettings().BgDark, loaded.BgDark);
            Assert.AreEqual(0, lines.Count, "a fresh install (no file anywhere) is the silent defaults case");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Load_TargetAbsentLegacyCorrupt_ReturnsTheDefaultsWithOneLine()
    {
        string dir = NewThemeTempDir();
        var lines = new List<string>();
        try
        {
            string target = Path.Combine(dir, "state", "app_theme.json");
            string legacy = Path.Combine(dir, "legacy.json");
            File.WriteAllText(legacy, "{ corrupt legacy");

            var loaded = ThemeSettings.Load(target, legacy, lines.Add);

            Assert.AreEqual(new ThemeSettings().BgDark, loaded.BgDark);
            Assert.IsFalse(File.Exists(target), "a corrupt legacy copy is never carried across");
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains(lines[0], "unparseable");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Load_TargetCorrupt_ReturnsTheDefaultsWithTheFallbackLine_AndNeverRepairsFromTheLegacy()
    {
        string dir = NewThemeTempDir();
        var lines = new List<string>();
        try
        {
            string target = Path.Combine(dir, "state.json");
            File.WriteAllText(target, "{ corrupt state file");
            string legacy = WriteThemeFile(dir, "legacy.json", "#A1A2A3");

            var loaded = ThemeSettings.Load(target, legacy, lines.Add);

            Assert.AreEqual(new ThemeSettings().BgDark, loaded.BgDark,
                "a corrupt state file degrades to the defaults, the pre-ADR-0021 corrupt-file rule");
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains(lines[0], "Theme load failed");
            Assert.IsFalse(lines.Any(l => l.Contains("Migrated legacy theme file")),
                "the migration runs only when the state file is ABSENT, never as a corrupt-file repair");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void MigrateLegacyCopy_SameTargetAndLegacyPath_ReturnsNull()
    {
        string dir = NewThemeTempDir();
        var lines = new List<string>();
        try
        {
            string path = WriteThemeFile(dir, "same.json", "#A1A2A3");

            var migrated = ThemeSettings.MigrateLegacyCopy(path, path, lines.Add);

            Assert.IsNull(migrated, "the same-file guard: a path cannot migrate into itself");
            Assert.AreEqual(0, lines.Count);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Load_TargetAbsentLegacyParseableButTheMigrationWriteFails_HonorsTheInMemoryCopyAndLogs()
    {
        string dir = NewThemeTempDir();
        var lines = new List<string>();
        try
        {
            // The target's parent is an existing FILE: Directory.CreateDirectory
            // throws, so the migration write fails (the read-only-dir shape,
            // no elevation needed).
            string blocker = Path.Combine(dir, "blocker");
            File.WriteAllText(blocker, "a file, not a directory");
            string target = Path.Combine(blocker, "app_theme.json");
            string legacy = WriteThemeFile(dir, "legacy.json", "#0A0B0C");

            var loaded = ThemeSettings.Load(target, legacy, lines.Add);

            Assert.AreEqual("#0A0B0C", loaded.BgDark,
                "a failed migration write still honors the colors the user last saw for this session");
            Assert.IsFalse(File.Exists(target), "nothing is written where the write failed");
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains(lines[0], "migration write");
            StringAssert.Contains(lines[0], "in-memory copy");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Load_TargetOverTheSizeCap_ReturnsTheDefaultsWithOneLine()
    {
        string dir = NewThemeTempDir();
        var lines = new List<string>();
        try
        {
            string target = Path.Combine(dir, "state.json");
            File.WriteAllBytes(target, new byte[ThemeSettings.MaxThemeFileBytes + 1]);
            string legacy = WriteThemeFile(dir, "legacy.json", "#A1A2A3");

            var loaded = ThemeSettings.Load(target, legacy, lines.Add);

            Assert.AreEqual(new ThemeSettings().BgDark, loaded.BgDark,
                "a hostile multi-GB theme file is rejected before the read, not after a multi-GB allocation");
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains(lines[0], "cap");
            Assert.IsFalse(lines.Any(l => l.Contains("Migrated legacy theme file")),
                "a present (even hostile) state file never triggers the migration");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Load_TargetIsJsonNull_ReturnsTheDefaultsWithOneLine()
    {
        string dir = NewThemeTempDir();
        var lines = new List<string>();
        try
        {
            // The truncated-write shape: Deserialize<ThemeSettings>("null")
            // returns null without throwing, so the silent-defaults hole gets
            // its own observable line.
            string target = Path.Combine(dir, "state.json");
            File.WriteAllText(target, "null");

            var loaded = ThemeSettings.Load(target, Path.Combine(dir, "absent-legacy.json"), lines.Add);

            Assert.AreEqual(new ThemeSettings().BgDark, loaded.BgDark);
            Assert.AreEqual(1, lines.Count, "a present-but-null state file is observable, not silent");
            StringAssert.Contains(lines[0], "deserialized to null");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void LoadFrom_OverTheSizeCap_ReturnsNull()
    {
        string dir = NewThemeTempDir();
        try
        {
            string path = Path.Combine(dir, "hostile.json");
            File.WriteAllBytes(path, new byte[ThemeSettings.MaxThemeFileBytes + 1]);

            Assert.IsNull(ThemeSettings.LoadFrom(path),
                "the read seam is capped before the read (the legacy leg degrades to the unparseable line)");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [TestMethod]
    public void Load_TargetAbsentLegacyOverCap_NotMigratedWithOneLine()
    {
        string dir = NewThemeTempDir();
        var lines = new List<string>();
        try
        {
            string target = Path.Combine(dir, "state", "app_theme.json");
            string legacy = Path.Combine(dir, "legacy.json");
            File.WriteAllBytes(legacy, new byte[ThemeSettings.MaxThemeFileBytes + 1]);

            var loaded = ThemeSettings.Load(target, legacy, lines.Add);

            Assert.AreEqual(new ThemeSettings().BgDark, loaded.BgDark, "an over-cap legacy copy is never carried across");
            Assert.IsFalse(File.Exists(target));
            Assert.AreEqual(1, lines.Count);
            StringAssert.Contains(lines[0], "unparseable or over the size cap");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
