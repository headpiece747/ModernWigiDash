using ModernWigiDash.App.Dialogs;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// Pins the theme dialog's draft module at its interface, without a window:
/// the entries seeded from the active theme (display order + display copy),
/// the single validity verdict, apply-to-settings (invalid values are never
/// written), and reset-to-defaults.
/// </summary>
[TestClass]
public class ThemeDraftTests
{
    [TestMethod]
    public void Ctor_Seeding_ReflectsTheActiveThemeValuesInDisplayOrder()
    {
        ThemeSettings.Theme = new ThemeSettings { AccentRed = "#FF0000", TextPrimary = "#111111" };
        var draft = new ThemeDraft();

        Assert.AreEqual(ThemeSettings.StringProperties.Count, draft.Entries.Count);
        Assert.AreEqual("#FF0000", draft.Entries.Single(e => e.Name == "AccentRed").Hex);
        Assert.AreEqual("#111111", draft.Entries.Single(e => e.Name == "TextPrimary").Hex);

        var displayOrder = draft.Entries
            .Select(e => (e.Group, e.Name))
            .OrderBy(e => e.Group, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
        CollectionAssert.AreEqual(
            displayOrder,
            draft.Entries.Select(e => (e.Group, e.Name)).ToList());

        var bgDark = draft.Entries.Single(e => e.Name == "BgDark");
        Assert.AreEqual("Surfaces", bgDark.Group);
        Assert.AreEqual(ThemePresentation.FriendlyName("BgDark"), bgDark.FriendlyName);
        Assert.AreEqual(ThemePresentation.Descriptions["BgDark"], bgDark.Description);
    }

    [TestMethod]
    public void UpdateHex_InvalidHex_InvalidatesAndNamesTheEntry()
    {
        ThemeSettings.Theme = new ThemeSettings();
        var draft = new ThemeDraft();
        Assert.IsTrue(draft.IsValid);
        Assert.IsNull(draft.InvalidEntryName);

        draft.UpdateHex("BgDark", "zzz");

        Assert.IsFalse(draft.IsValid);
        Assert.AreEqual("BgDark", draft.InvalidEntryName);
    }

    [TestMethod]
    public void UpdateHex_ValidHexAfterInvalid_RevalidatesTheDraft()
    {
        ThemeSettings.Theme = new ThemeSettings();
        var draft = new ThemeDraft();
        draft.UpdateHex("BgDark", "zzz");
        Assert.IsFalse(draft.IsValid);

        draft.UpdateHex("BgDark", "#123456");

        Assert.IsTrue(draft.IsValid);
        Assert.IsNull(draft.InvalidEntryName);
    }

    [TestMethod]
    public void ApplyToSettings_WritesOnlyTheParseableValues()
    {
        var theme = new ThemeSettings();
        ThemeSettings.Theme = theme;
        var draft = new ThemeDraft();
        string bgDarkBefore = theme.BgDark;

        draft.UpdateHex("BgDark", "zzz");
        draft.UpdateHex("BgPanel", "#010203");
        draft.ApplyToSettings();

        Assert.AreEqual(bgDarkBefore, theme.BgDark); // the invalid value never reaches the theme
        Assert.AreEqual("#010203", theme.BgPanel);
    }

    [TestMethod]
    public void ResetToDefaults_RestoresEveryEntryToTheDefaults()
    {
        ThemeSettings.Theme = new ThemeSettings { AccentRed = "#FF0000", Border = "#111111" };
        var draft = new ThemeDraft();
        var defaults = new ThemeSettings();

        draft.ResetToDefaults();

        Assert.AreEqual(defaults.AccentRed, draft.Entries.Single(e => e.Name == "AccentRed").Hex);
        Assert.AreEqual(defaults.Border, draft.Entries.Single(e => e.Name == "Border").Hex);
        Assert.IsTrue(draft.Entries.All(e => ThemeSettings.ParseColor(e.Hex) is not null));
    }
}
