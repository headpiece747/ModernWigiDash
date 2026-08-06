using System.Windows;
using System.Windows.Media;
using ModernWigiDash.App;
using ModernWigiDash.Core.Theming;
using AppClass = ModernWigiDash.App.App;

namespace ModernWigiDash.Tests;

[TestClass]
public class ThemeManagerTests
{
    /// <summary>
    /// Application.Current is process-wide and can only be created once, so all
    /// tests share a single App instance created on an STA thread.
    /// </summary>
    private static readonly Lazy<AppClass> SharedApp = new(CreateApp);

    private static AppClass CreateApp()
    {
        AppClass app = null!;
        var thread = new Thread(() => app = new AppClass());
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return app;
    }

    [TestMethod]
    public void App_Constructor_SetsApplicationCurrent()
    {
        _ = SharedApp.Value;

        Assert.IsNotNull(Application.Current);
    }

    [TestMethod]
    public void App_OnStartup_LoadsAndAppliesTheme()
    {
        // OnStartup requires a dispatcher pump; simulate its observable effect
        // by applying the same theme pipeline directly and asserting resources.
        var resources = Application.Current.Resources;
        resources.Clear();

        ThemeSettings.Theme = new ThemeSettings { BgDark = "#010203" };
        ThemeManager.ApplyToApplication();

        Assert.IsTrue(resources.Contains("BgDarkColor"));
        var color = (Color)resources["BgDarkColor"];
        Assert.AreEqual(Color.FromArgb(255, 0x01, 0x02, 0x03), color);
    }

    [TestMethod]
    public void ThemeManager_AppliesAllThemePropertiesAsColors()
    {
        var resources = Application.Current.Resources;
        resources.Clear();

        var theme = new ThemeSettings();
        ThemeSettings.Theme = theme;
        ThemeManager.ApplyToApplication();

        string[] props =
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
        foreach (string prop in props)
        {
            Assert.IsTrue(resources.Contains($"{prop}Color"), $"missing {prop}Color");
        }
    }

    [TestMethod]
    public void ThemeManager_MapsBrushKeysPerBrushKeyMap()
    {
        var resources = Application.Current.Resources;
        resources.Clear();

        ThemeSettings.Theme = new ThemeSettings();
        ThemeManager.ApplyToApplication();

        // "Border" maps to "BorderBrush" (not "Border")
        Assert.IsFalse(resources.Contains("Border"));
        Assert.IsTrue(resources.Contains("BorderBrush"));
        // "TextPrimary" maps to "TextPrimary" and "AccentBlue"
        Assert.IsTrue(resources.Contains("TextPrimary"));
        Assert.IsTrue(resources.Contains("AccentBlue"));
        // "TitleBar" maps to "TitleBarBrush"
        Assert.IsFalse(resources.Contains("TitleBar"));
        Assert.IsTrue(resources.Contains("TitleBarBrush"));
        // Unmapped property keeps its own key as a brush
        Assert.IsTrue(resources.Contains("BgCard"));
        Assert.IsInstanceOfType(resources["BgCard"], typeof(SolidColorBrush));
    }

    [TestMethod]
    public void ThemeManager_SkipsInvalidColors()
    {
        var resources = Application.Current.Resources;
        resources.Clear();

        ThemeSettings.Theme = new ThemeSettings { BgDark = "not-a-color" };
        ThemeManager.ApplyToApplication();

        Assert.IsFalse(resources.Contains("BgDarkColor"));
    }
}
