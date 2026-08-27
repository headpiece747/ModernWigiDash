using System.Text.Json;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// The export bundle's theme section (ADR-0021): how the theme rides a
/// profile export and how an import reads it back, pinned at the module
/// interface without a window, a file dialog, or a theme file on disk.
/// </summary>
[TestClass]
public class ProfileExportThemeTests
{
    private static ProfileLayout SampleProfile()
    {
        var profile = new ProfileLayout();
        ProfileOps.AddPage(profile, "Bundle Page");
        return profile;
    }

    private static ThemeSettings FullTheme()
        => new()
        {
            BgDark = "#111111",
            BgPanel = "#222222",
            BgCard = "#333333",
            Border = "#444444",
            AccentRed = "#555555",
            M3Primary = "#666666",
            M3PrimaryContainer = "#777777",
            M3OnPrimaryContainer = "#888888",
            AccentGreen = "#999999",
            TextPrimary = "#AAAAAA",
            TextSecondary = "#BBBBBB",
            ControlHover = "#CCCCCC",
            DropdownHover = "#DDDDDD",
            TitleBar = "#EEEEEE",
            StatusBarBackground = "#F0F0F0",
            DangerBackground = "#F11111",
            DangerBorder = "#F22222",
            SuccessBackground = "#F33333",
            SuccessBorder = "#F44444"
        };

    [TestMethod]
    public void WithTheme_PlacesTheSectionUnderTheLiteralThemeKey()
    {
        string bundle = ProfileExportTheme.WithTheme(ProfileOps.ExportJson(SampleProfile()), FullTheme());

        using var doc = JsonDocument.Parse(bundle);
        Assert.IsTrue(doc.RootElement.TryGetProperty("theme", out _),
            "the section rides the export under the literal 'theme' key (the one spelling the import reads back)");
    }

    [TestMethod]
    public void WithThemeThenReadTheme_RoundTripsEveryThemeProperty()
    {
        var theme = FullTheme();
        string bundle = ProfileExportTheme.WithTheme(ProfileOps.ExportJson(SampleProfile()), theme);

        var read = ProfileExportTheme.ReadTheme(bundle);

        Assert.IsNotNull(read, "the section must round-trip");
        Assert.AreEqual(
            ThemeApplicator.Fingerprint(theme),
            ThemeApplicator.Fingerprint(read),
            "every themeable property must survive the bundle round trip (the fingerprint is the one change signal)");
        Assert.AreEqual("#111111", read.BgDark);
        Assert.AreEqual("#F44444", read.SuccessBorder);
    }

    [TestMethod]
    public void WithTheme_KeepsTheProfileFieldsAtTheRoot()
    {
        string bundle = ProfileExportTheme.WithTheme(ProfileOps.ExportJson(SampleProfile()), FullTheme());

        using var doc = JsonDocument.Parse(bundle);
        Assert.AreEqual(JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.IsTrue(doc.RootElement.TryGetProperty("ProfileName", out _), "the profile's own fields stay at the root");
        Assert.IsTrue(doc.RootElement.TryGetProperty("Pages", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty(ProfileExportTheme.JsonKey, out var themeNode));
        Assert.AreEqual(JsonValueKind.Object, themeNode.ValueKind);
    }

    [TestMethod]
    public void ReadTheme_BareProfileJson_ReturnsNull()
        => Assert.IsNull(ProfileExportTheme.ReadTheme(ProfileOps.ExportJson(SampleProfile())),
            "legacy exports and the app's own profile.json never carry the section");

    [TestMethod]
    public void ReadTheme_AbsentOrUnshapedSection_ReturnsNull()
    {
        Assert.IsNull(ProfileExportTheme.ReadTheme("{\"theme\":null}"), "a null-valued section is absent");
        Assert.IsNull(ProfileExportTheme.ReadTheme("{\"theme\":\"not-a-theme\"}"), "a string section is unshaped");
        Assert.IsNull(ProfileExportTheme.ReadTheme("{\"theme\":[1,2,3]}"), "an array section is unshaped");
    }

    [TestMethod]
    public void ReadTheme_CorruptOrNonObjectJson_ReturnsNullWithoutThrowing()
    {
        Assert.IsNull(ProfileExportTheme.ReadTheme("{ corrupt"));
        Assert.IsNull(ProfileExportTheme.ReadTheme("\"a string root\""));
        Assert.IsNull(ProfileExportTheme.ReadTheme("42"));
    }

    [TestMethod]
    public void WithTheme_UnparseableInput_PassesThroughUnchanged()
    {
        string junk = "{ not valid json";
        Assert.AreEqual(junk, ProfileExportTheme.WithTheme(junk, FullTheme()),
            "a malformed export passes through untouched rather than failing the export");
    }

    [TestMethod]
    public void ExportJson_BareProfile_NeverCarriesTheThemeSection()
    {
        string json = ProfileOps.ExportJson(SampleProfile());

        Assert.IsFalse(json.Contains("\"theme\"", StringComparison.Ordinal),
            "the persisted profile.json must stay bare: the theme rides only the manual export bundle (ADR-0021)");
    }
}
