using System.Windows;
using System.Windows.Controls;
using ModernWigiDash.App.Dialogs;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// Thin wiring pins for the settings hub as a forwarder: the display facts
/// are pinned at the <see cref="SettingsModel"/> interface in
/// SettingsModelTests; these tests only pin that the window seeds the
/// checked radio from the persisted value (without committing), seeds the
/// Start-with-Windows checkbox from the store's presence (without
/// committing, ADR-0019), writes a radio check and a checkbox toggle through
/// to their commit seams, and routes the Profile group's buttons to their
/// seams.
/// </summary>
[TestClass]
public class SettingsDialogTests
{
    private static readonly StaHost Host = new("SettingsDialogTests-STA");

    private static (SettingsDialog Dialog, List<string> Commits, List<bool> AutostartCommits, List<string> Clicked) Build(
        string? persistedCloseBehavior,
        bool seededAutostart,
        List<string> commits,
        List<bool> autostartCommits,
        List<string> clicked)
    {
        ThemeSettings.Theme = new ThemeSettings();
        var owner = new Window();
        WpfWindow.ShowOwner(owner);
        var dialog = new SettingsDialog(
            owner,
            new ThemeApplicator(),
            persistedCloseBehavior,
            value => commits.Add(value),
            seededAutostart,
            value => autostartCommits.Add(value),
            () => clicked.Add("export"),
            () => clicked.Add("import"));
        dialog.Show(); // a Window's visual tree exists only after it is shown
        dialog.UpdateLayout(); // force the synchronous layout pass before walking the tree
        return (dialog, commits, autostartCommits, clicked);
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
            var (dialog, _, _, _) = Build(CloseBehaviorPolicy.HideToTray, false, [], [], []);
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
                var (dialog, _, _, _) = Build(persisted, false, [], [], []);
                Assert.IsTrue(RadioFor(dialog, CloseBehaviorPolicy.Default).IsChecked == true);
                dialog.Close();
            }
            return null;
        });

    [TestMethod]
    public void Ctor_FiresNoCommitWhenSeeding()
        => Host.Run<object?>(() =>
        {
            var (dialog, commits, _, _) = Build(CloseBehaviorPolicy.HideToTray, false, [], [], []);
            Assert.AreEqual(0, commits.Count);
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void RadioCheck_WritesTheValueThroughToTheCommitSeam()
        => Host.Run<object?>(() =>
        {
            var (dialog, commits, _, _) = Build(null, false, [], [], []);
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
            var (dialog, _, _, clicked) = Build(null, false, [], [], []);
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
            var (dialog, _, _, _) = Build(null, false, [], [], []);
            var buttons = dialog.FindVisualChildren<Button>().ToList();
            Assert.IsTrue(buttons.Any(b => string.Equals(b.Content as string, "Customize theme colors...", StringComparison.Ordinal)));
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
            var (dialog, _, autostartCommits, _) = Build(null, true, [], [], []);
            Assert.IsTrue(AutostartCheckBox(dialog).IsChecked == true, "the entry's presence seeds the checkbox");
            Assert.AreEqual(0, autostartCommits.Count, "the seed, like the radio seed, commits nothing");
            dialog.Close();
            return null;
        });

    [TestMethod]
    public void AutostartCheckbox_WritesThroughToTheCommitSeam()
        => Host.Run<object?>(() =>
        {
            var (dialog, _, autostartCommits, _) = Build(null, false, [], [], []);
            Assert.IsTrue(AutostartCheckBox(dialog).IsChecked == false, "an absent entry seeds the checkbox unchecked");
            AutostartCheckBox(dialog).IsChecked = true;
            CollectionAssert.AreEqual(new[] { true }, autostartCommits, "checking commits the enabled state");
            AutostartCheckBox(dialog).IsChecked = false;
            CollectionAssert.AreEqual(new[] { true, false }, autostartCommits, "unchecking commits the disabled state");
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
