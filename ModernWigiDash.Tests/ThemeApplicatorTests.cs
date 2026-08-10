using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// The theme-applicator rules. The pure decisions (the preview-shadow accent,
/// the changed-theme fingerprint) are pinned without WPF; one STA test drives
/// the end-to-end shadow re-application through a namescoped preview frame.
/// </summary>
[TestClass]
public class ThemeApplicatorTests
{
    [TestMethod]
    public void PreviewShadowAccent_ValidHex_ReturnsParsedColor()
    {
        var theme = new ThemeSettings { AccentRed = "#010203" };

        Assert.AreEqual(new RgbaColor(255, 0x01, 0x02, 0x03), ThemeApplicator.PreviewShadowAccent(theme));
    }

    [TestMethod]
    public void PreviewShadowAccent_InvalidHex_ReturnsNull_ShadowKeepsCurrentColor()
    {
        var theme = new ThemeSettings { AccentRed = "not-a-color" };

        Assert.IsNull(ThemeApplicator.PreviewShadowAccent(theme),
            "an invalid accent must leave the shadow's current color untouched");
    }

    [TestMethod]
    public void Fingerprint_ChangedColor_Changes()
    {
        var before = new ThemeSettings { AccentRed = "#010203" };
        var after = new ThemeSettings { AccentRed = "#FF0000" };

        Assert.AreNotEqual(ThemeApplicator.Fingerprint(before), ThemeApplicator.Fingerprint(after));
    }

    [TestMethod]
    public void Fingerprint_UnchangedTheme_IsStable()
    {
        var theme = new ThemeSettings { BgDark = "#010203", AccentRed = "#AABBCC" };

        Assert.AreEqual(ThemeApplicator.Fingerprint(theme), ThemeApplicator.Fingerprint(theme));
    }

    [TestMethod]
    public void Apply_ThemeChanged_ReappliesPreviewShadowAccent()
    {
        RunOnSta(() =>
        {
            var window = new Window
            {
                Icon = new DrawingImage(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 16, 16))))
            };
            var preview = new Border { Effect = new DropShadowEffect { Color = Colors.White } };
            var scope = new NameScope();
            NameScope.SetNameScope(window, scope);
            scope.RegisterName("PreviewFrame", preview);

            var applicator = new ThemeApplicator();
            ThemeSettings.Theme = new ThemeSettings { AccentRed = "#010203" };

            applicator.Apply(window);
            Assert.AreEqual(Color.FromArgb(255, 0x01, 0x02, 0x03), ((DropShadowEffect)preview.Effect).Color);

            ThemeSettings.Theme = new ThemeSettings { AccentRed = "#FF0000" };
            applicator.Apply(window);
            Assert.AreEqual(Color.FromRgb(0xFF, 0, 0), ((DropShadowEffect)preview.Effect).Color,
                "DropShadowEffect does not track DynamicResource — the applicator must re-derive the accent on theme change");
        });
    }

    private static void RunOnSta(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            Assert.Fail($"STA work failed: {error}");
        }
    }
}
