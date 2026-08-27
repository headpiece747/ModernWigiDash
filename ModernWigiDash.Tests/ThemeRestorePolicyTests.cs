using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// The export bundle's theme-restore offer rule pinned at its interface
/// (ADR-0021): the offer fires only when the bundle carries a theme that
/// differs from the current one (the applicator's fingerprint is the one
/// change signal). No window, no dialog, no apply sequence.
/// </summary>
[TestClass]
public class ThemeRestorePolicyTests
{
    [TestMethod]
    public void ShouldOffer_BundledIsNull_ReturnsFalse()
        => Assert.IsFalse(ThemeRestorePolicy.ShouldOffer(null, new ThemeSettings()),
            "a bundle that carries no theme offers nothing");

    [TestMethod]
    public void ShouldOffer_BundledMatchesCurrent_ReturnsFalse()
    {
        var current = new ThemeSettings();
        var bundled = new ThemeSettings();

        Assert.IsFalse(ThemeRestorePolicy.ShouldOffer(bundled, current),
            "a default-theme export must not prompt a default-themed machine");
    }

    [TestMethod]
    public void ShouldOffer_BundledDiffersFromCurrent_ReturnsTrue()
    {
        var current = new ThemeSettings();
        var bundled = new ThemeSettings { BgDark = "#FF0000" };

        Assert.IsTrue(ThemeRestorePolicy.ShouldOffer(bundled, current),
            "a differing theme is worth one confirm");
    }
}
