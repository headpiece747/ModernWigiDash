using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernWigiDash.App.Controls;
using ModernWigiDash.App.Dialogs;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// Thin wiring pins for the settings hub as a forwarder over its named host
/// seam: the display facts are pinned at the <see cref="SettingsModel"/>
/// interface in SettingsModelTests; these tests bind an in-memory fake
/// <see cref="ISettingsHubHost"/> and only pin that the hub seeds the
/// checked radio from the persisted value (without committing), seeds the
/// Start-with-Windows and kill-switch checkboxes and the AutoHotkey path
/// box from their persisted states (without committing, ADR-0019), writes a
/// radio check and the checkbox toggles through to their commit seams,
/// commits the AHK path on focus loss, and routes the Profile group's
/// buttons and the AHK Browse button to their seams (a chosen path rides
/// back into the box, a cancel leaves it untouched). The window's
/// production-host side of the seam is the explicit forward to the window's
/// write-through seams, which the seams' own pins cover (WindowAutostartTests,
/// WindowAhkScriptTests).
/// </summary>
[TestClass]
public class SettingsDialogTests
{
    private static readonly StaHost Host = new("SettingsDialogTests-STA");

    // The Browse seam's chosen-path default (the cancel pin passes null):
    // the hub writes a chosen path back into the box, so the routing pin
    // asserts the displayed path followed the persisted one.
    private const string ChosenBrowsePath = @"C:\Chosen\autohotkey.exe";

    /// <summary>The in-memory fake host the hub's seam drives against: the
    /// open-time seeds plus a capture list per commit member (the Browse
    /// seam also records its click and answers with the canned path).</summary>
    private sealed class FakeHubHost : ISettingsHubHost
    {
        public FakeHubHost(SettingsHubSeed seed, string? browseResult)
        {
            Seed = seed;
            BrowseResult = browseResult;
        }

        public SettingsHubSeed Seed { get; }
        public string? BrowseResult { get; }
        public List<string> CloseBehaviorCommits { get; } = [];
        public List<bool> AutostartCommits { get; } = [];
        public List<bool> KillSwitchCommits { get; } = [];
        public List<string> AhkCommits { get; } = [];
        public List<string> Clicked { get; } = [];
        public List<string> PageBgCommits { get; } = [];
        public List<bool> MinimizeToTrayCommits { get; } = [];

        public void CommitCloseBehavior(string value) => CloseBehaviorCommits.Add(value);
        public void CommitAutostart(bool enabled) => AutostartCommits.Add(enabled);
        public void CommitKillSwitch(bool tripped) => KillSwitchCommits.Add(tripped);
        public void CommitAhkInterpreter(string path) => AhkCommits.Add(path);
        public string? BrowseAhkInterpreter() { Clicked.Add("browse"); return BrowseResult; }
        public void ExportProfile() => Clicked.Add("export");
        public void ImportProfile() => Clicked.Add("import");
        public void CommitPageBackground(string hex) => PageBgCommits.Add(hex);
        public void CommitMinimizeToTrayOnStartup(bool enabled) => MinimizeToTrayCommits.Add(enabled);
    }

    private static (SettingsDialog Dialog, List<string> Commits, List<bool> AutostartCommits, List<bool> KillSwitchCommits, List<string> AhkCommits, List<string> Clicked, List<string> PageBgCommits, List<bool> MinimizeToTrayCommits) Build(
        string? persistedCloseBehavior,
        bool seededAutostart,
        bool seededKillSwitch,
        string seededAhkPath,
        string seededPageBackground = ModernWigiDash.Core.Models.PageLayout.DefaultBackgroundHexColor,
        string? browseResult = ChosenBrowsePath,
        bool seededMinimizeToTray = false)
    {
        ThemeSettings.Theme = new ThemeSettings();
        var owner = new Window();
        WpfWindow.ShowOwner(owner);
        var host = new FakeHubHost(
            new SettingsHubSeed(persistedCloseBehavior, seededAutostart, seededKillSwitch, seededAhkPath, seededPageBackground, seededMinimizeToTray),
            browseResult);
        var dialog = new SettingsDialog(owner, new ThemeApplicator(), host);
        dialog.Show(); // a Window's visual tree exists only after it is shown
        dialog.UpdateLayout(); // force the synchronous layout pass before walking the tree
        return (dialog, host.CloseBehaviorCommits, host.AutostartCommits, host.KillSwitchCommits, host.AhkCommits, host.Clicked, host.PageBgCommits, host.MinimizeToTrayCommits);
    }

    private static RadioButton RadioFor(SettingsDialog dialog, string value)
        => dialog.FindVisualChildren<RadioButton>()
            .Single(r => string.Equals(r.Content as string, LabelFor(value), StringComparison.Ordinal));

    private static string LabelFor(string value)
        => SettingsModel.CloseBehaviors.Single(o => o.Value == value).Label;

    [TestMethod]
    public void Ctor_SeedsTheCheckedRadioFromThePersistedValue()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, _, _, _, _) = Build(CloseBehaviorPolicy.HideToTray, false, false, "");
            Assert.IsTrue(RadioFor(dialog, CloseBehaviorPolicy.HideToTray).IsChecked == true);
            Assert.IsTrue(RadioFor(dialog, CloseBehaviorPolicy.Quit).IsChecked == false);
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void Ctor_SeedsTheDefaultRadioWhenThePersistedValueIsAbsentOrUnknown()
        => Host.Run<object?>(() =>
        {
            foreach (var persisted in new string?[] { null, "", "QUIT", "bogus" })
            {
                var (dialog, _, _, _, _, _, _, _) = Build(persisted, false, false, "");
                Assert.IsTrue(RadioFor(dialog, CloseBehaviorPolicy.Default).IsChecked == true);
                dialog.Close();
            }
            return null;
        });

    [TestMethod]
    public void Ctor_FiresNoCommitWhenSeeding()
        => Host.Run<object?>(() =>
        {
            var (dialog, commits, _, _, _, _, _, _) = Build(CloseBehaviorPolicy.HideToTray, false, false, "");
            Assert.AreEqual(0, commits.Count);
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void RadioCheck_WritesTheValueThroughToTheCommitSeam()
        => Host.Run<object?>(() =>
        {
            var (dialog, commits, _, _, _, _, _, _) = Build(null, false, false, "");
            RadioFor(dialog, CloseBehaviorPolicy.HideToTray).IsChecked = true;
            CollectionAssert.AreEqual(new[] { CloseBehaviorPolicy.HideToTray }, commits);
            RadioFor(dialog, CloseBehaviorPolicy.Quit).IsChecked = true;
            CollectionAssert.AreEqual(new[] { CloseBehaviorPolicy.HideToTray, CloseBehaviorPolicy.Quit }, commits);
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void ProfileButtons_RouteToTheirSeams()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, _, clicked, _, _) = Build(null, false, false, "");
            var export = dialog.FindVisualChildren<Button>().First(b => string.Equals(b.Content as string, "Export profile...", StringComparison.Ordinal));
            export.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var import = dialog.FindVisualChildren<Button>().First(b => string.Equals(b.Content as string, "Import profile...", StringComparison.Ordinal));
            import.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CollectionAssert.AreEqual(new[] { "export", "import" }, clicked);
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void AppearanceGroup_ExposesTheThemeEditorButton()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, _, _, _, _) = Build(null, false, false, "");
            var buttons = dialog.FindVisualChildren<Button>().ToList();
            Assert.IsTrue(buttons.Any(b => string.Equals(b.Content as string, "Customize theme colors...", StringComparison.Ordinal)));
            dialog.Close();
            return null;
        });

    private static ColorPickerEditor PageBackgroundEditor(SettingsDialog dialog)
        => dialog.FindVisualChildren<ColorPickerEditor>().Single();

    [TestMethod]
    public void Ctor_SeedsThePageBackgroundPickerFromTheActivePage_WithoutCommitting()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, _, _, pageBgCommits, _) = Build(null, false, false, "", seededPageBackground: "#123456");
            Assert.AreEqual("#123456", PageBackgroundEditor(dialog).Hex,
                "the row seeds from the active page's persisted background");
            Assert.AreEqual(0, pageBgCommits.Count, "the seed, like the other seeds, commits nothing");
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void PageBackgroundPicker_WritesThroughToTheCommitSeam()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, _, _, pageBgCommits, _) = Build(null, false, false, "", seededPageBackground: "#123456");
            PageBackgroundEditor(dialog).HexBox.Text = "#654321";
            CollectionAssert.AreEqual(new[] { "#654321" }, pageBgCommits,
                "a valid hex change commits the new color the moment it lands");
            dialog.Close();
            return null;
        });

    private static CheckBox AutostartCheckBox(SettingsDialog dialog)
        => dialog.FindVisualChildren<CheckBox>()
            .Single(c => string.Equals(c.Content as string, "Start with Windows", StringComparison.Ordinal));

    [TestMethod]
    public void Ctor_SeedsTheAutostartCheckboxFromTheStoreState_WithoutCommitting()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, autostartCommits, _, _, _, _, _) = Build(null, true, false, "");
            Assert.IsTrue(AutostartCheckBox(dialog).IsChecked == true, "the entry's presence seeds the checkbox");
            Assert.AreEqual(0, autostartCommits.Count, "the seed, like the radio seed, commits nothing");
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void AutostartCheckbox_WritesThroughToTheCommitSeam()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, autostartCommits, _, _, _, _, _) = Build(null, false, false, "");
            Assert.IsTrue(AutostartCheckBox(dialog).IsChecked == false, "an absent entry seeds the checkbox unchecked");
            AutostartCheckBox(dialog).IsChecked = true;
            CollectionAssert.AreEqual(new[] { true }, autostartCommits, "checking commits the enabled state");
            AutostartCheckBox(dialog).IsChecked = false;
            CollectionAssert.AreEqual(new[] { true, false }, autostartCommits, "unchecking commits the disabled state");
            dialog.Close();
            return null;
        });

    private static CheckBox KillSwitchCheckBox(SettingsDialog dialog)
            => dialog.FindVisualChildren<CheckBox>()
                .Single(c => string.Equals(c.Content as string, "Kill Switch", StringComparison.Ordinal));

    /// <summary>The AHK interpreter path box. The Appearance group's
    /// page-background picker hosts a hex box of its own, so the picker's
    /// descendant TextBoxes are excluded by the ancestor check.</summary>
    private static TextBox AhkPathTextBox(SettingsDialog dialog)
        => dialog.FindVisualChildren<TextBox>()
            .Single(tb => !HasAncestor<ColorPickerEditor>(tb));

    private static bool HasAncestor<T>(DependencyObject node)
        where T : DependencyObject
    {
        var current = node;
        while (VisualTreeHelper.GetParent(current) is { } parent)
        {
            if (parent is T) return true;
            current = parent;
        }
        return false;
    }

    [TestMethod]
    public void Ctor_SeedsTheKillSwitchCheckbox_WithoutCommitting()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, killSwitchCommits, _, _, _, _) = Build(null, false, true, "");
            Assert.IsTrue(KillSwitchCheckBox(dialog).IsChecked == true, "a tripped kill switch seeds the checkbox checked");
            Assert.AreEqual(0, killSwitchCommits.Count, "the seed, like the other seeds, commits nothing");
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void KillSwitchCheckbox_WritesThroughToTheCommitSeam()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, killSwitchCommits, _, _, _, _) = Build(null, false, false, "");
            Assert.IsTrue(KillSwitchCheckBox(dialog).IsChecked == false, "the integration defaults live (unchecked)");
            KillSwitchCheckBox(dialog).IsChecked = true;
            CollectionAssert.AreEqual(new[] { true }, killSwitchCommits, "tripping commits true");
            KillSwitchCheckBox(dialog).IsChecked = false;
            CollectionAssert.AreEqual(new[] { true, false }, killSwitchCommits, "releasing commits false");
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void Ctor_SeedsTheAhkPathBox_WithoutCommitting()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, ahkCommits, _, _, _) = Build(null, false, false, @"C:\Tools\autohotkey.exe");
            Assert.AreEqual(@"C:\Tools\autohotkey.exe", AhkPathTextBox(dialog).Text);
            Assert.AreEqual(0, ahkCommits.Count, "the seed commits nothing");
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void AhkPathBox_LostFocusCommitsTheTrimmedValue()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, ahkCommits, _, _, _) = Build(null, false, false, "");
            AhkPathTextBox(dialog).Text = "  C:\\Tools\\autohotkey.exe  ";
            AhkPathTextBox(dialog).RaiseEvent(new RoutedEventArgs(TextBox.LostFocusEvent));
            CollectionAssert.AreEqual(new[] { @"C:\Tools\autohotkey.exe" }, ahkCommits,
                "focus loss commits the trimmed value (an untrimmed path would not resolve)");
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void AhkBrowseButton_RoutesToTheBrowseSeam_AndWritesTheChosenPathBackIntoTheBox()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, _, clicked, _, _) = Build(null, false, false, "");
            var browse = dialog.FindVisualChildren<Button>()
                .Single(b => string.Equals(b.Content as string, "Browse...", StringComparison.Ordinal));
            browse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CollectionAssert.AreEqual(new[] { "browse" }, clicked, "Browse routes to the window's file-dialog seam");
            Assert.AreEqual(ChosenBrowsePath, AhkPathTextBox(dialog).Text,
                "the chosen path rides back into the box (the displayed path cannot drift from the persisted one)");
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void AhkBrowseButton_CancelLeavesTheBoxUntouched()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, _, clicked, _, _) = Build(null, false, false, @"C:\Seeded\autohotkey.exe", browseResult: null);
            var browse = dialog.FindVisualChildren<Button>()
                .Single(b => string.Equals(b.Content as string, "Browse...", StringComparison.Ordinal));
            browse.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            CollectionAssert.AreEqual(new[] { "browse" }, clicked);
            Assert.AreEqual(@"C:\Seeded\autohotkey.exe", AhkPathTextBox(dialog).Text,
                "a cancel returns null and leaves the box (and the setting) untouched");
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void MinimizeToTrayCheckbox_SeededChecked_CommitsTrueOnUncheck()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, _, _, _, minimizeCommits) = Build(null, false, false, "", seededMinimizeToTray: true);
            var cb = dialog.FindVisualChildren<CheckBox>()
                .Single(c => string.Equals(c.Content as string, "Minimize to tray on startup", StringComparison.Ordinal));
            Assert.IsTrue(cb.IsChecked == true, "the checkbox seeds checked from the persisted flag");
            cb.IsChecked = false;
            CollectionAssert.AreEqual(new[] { false }, minimizeCommits,
                "unchecking commits false through the seam");
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void MinimizeToTrayCheckbox_SeededUnchecked_CommitsTrueOnCheck()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, _, _, _, _, _, minimizeCommits) = Build(null, false, false, "");
            var cb = dialog.FindVisualChildren<CheckBox>()
                .Single(c => string.Equals(c.Content as string, "Minimize to tray on startup", StringComparison.Ordinal));
            Assert.IsFalse(cb.IsChecked == true, "the checkbox seeds unchecked by default");
            cb.IsChecked = true;
            CollectionAssert.AreEqual(new[] { true }, minimizeCommits,
                "checking commits true through the seam");
            dialog.Close();
            return null;
        });

    /// <summary>
    /// Leaves the process without an Application so other test classes (whose
    /// SharedApp Lazy unconditionally calls new App()) can still create theirs.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        Host.DetachApplication();
    }
}
