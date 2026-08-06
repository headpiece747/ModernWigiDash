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
        Assert.AreEqual("Card / Input Background", ThemeSettings.FriendlyName("BgCard"));
        Assert.AreEqual("UnknownProp", ThemeSettings.FriendlyName("UnknownProp"));
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
            Assert.IsTrue(ThemeSettings.DisplayNames.ContainsKey(name), $"DisplayNames missing {name}");
            Assert.IsTrue(ThemeSettings.Descriptions.ContainsKey(name), $"Descriptions missing {name}");
            Assert.IsTrue(ThemeSettings.Groups.ContainsKey(name), $"Groups missing {name}");
        }
    }
}
