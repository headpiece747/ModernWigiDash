using System.Windows;
using System.Windows.Controls;
using ModernWigiDash.App.Controls;
using ModernWigiDash.App.Dialogs;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

/// <summary>
/// Thin wiring pins for the theme dialog as a forwarder: the decision rules
/// (entries, validity verdict, apply, reset) are pinned at the
/// <see cref="ThemeDraft"/> interface in ThemeDraftTests; these tests only
/// pin that the window builds one editor per entry and forwards the editor's
/// changes and the Reset click into the draft.
/// </summary>
[TestClass]
public class ThemeDialogTests
{
    private static readonly StaHost Host = new("ThemeDialogTests-STA");

    [TestMethod]
    public void Ctor_BuildsOneEditorPerEntry_SeededFromTheActiveTheme()
        => Host.Run<object?>(() =>
        {
            ThemeSettings.Theme = new ThemeSettings { AccentGreen = "#123456" };
            var owner = new Window();
            WpfWindow.ShowOwner(owner);
            var dialog = new ThemeDialog(owner, new ThemeApplicator());
            dialog.Show(); // a Window's visual tree exists only after it is shown
            dialog.UpdateLayout(); // force the synchronous layout pass before walking the tree
            var editors = dialog.FindVisualChildren<ColorPickerEditor>().ToList();
            Assert.AreEqual(ThemeSettings.StringProperties.Count, editors.Count);
            // AccentGreen is the first entry in group-then-name display order,
            // so the seed must have reached its editor.
            Assert.AreEqual("#123456", editors[0].Hex);
            return null;
        });

    [TestMethod]
    public void EditorTextChange_ForwardsTheValidityVerdictToTheApplyButton()
        => Host.Run<object?>(() =>
        {
            ThemeSettings.Theme = new ThemeSettings();
            var owner = new Window();
            WpfWindow.ShowOwner(owner);
            var dialog = new ThemeDialog(owner, new ThemeApplicator());
            dialog.Show(); // a Window's visual tree exists only after it is shown
            dialog.UpdateLayout(); // force the synchronous layout pass before walking the tree
            var editor = dialog.FindVisualChildren<ColorPickerEditor>().First();
            editor.HexBox.Text = "zzz";
            Assert.IsFalse(dialog.ApplyIsEnabledForTest);
            editor.HexBox.Text = "#F59E0B";
            Assert.IsTrue(dialog.ApplyIsEnabledForTest);
            return null;
        });

    [TestMethod]
    public void ResetClick_SyncsEveryEditorToTheDraftsDefaults()
        => Host.Run<object?>(() =>
        {
            ThemeSettings.Theme = new ThemeSettings();
            var owner = new Window();
            WpfWindow.ShowOwner(owner);
            var dialog = new ThemeDialog(owner, new ThemeApplicator());
            dialog.Show(); // a Window's visual tree exists only after it is shown
            dialog.UpdateLayout(); // force the synchronous layout pass before walking the tree
            var editors = dialog.FindVisualChildren<ColorPickerEditor>().ToList();
            editors[0].HexBox.Text = "zzz";
            var reset = dialog.FindVisualChildren<Button>().First(b => b.Content as string == "Reset");
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < editors.Count; i++)
                Assert.AreEqual(dialog.DraftForTest.Entries[i].Hex, editors[i].Hex);
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
