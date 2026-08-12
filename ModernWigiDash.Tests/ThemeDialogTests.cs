using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ModernWigiDash.App.Controls;
using ModernWigiDash.App.Dialogs;
using ModernWigiDash.App.Theming;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

[TestClass]
public class ThemeDialogTests
{
    private static readonly StaHost Host = new("ThemeDialogTests-STA");

    [TestMethod]
    public void Ctor_BuildsOneColorEditorPerThemeProperty()
        => Host.Run<object?>(() =>
        {
            var owner = new Window();
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            var dialog = new ThemeDialog(owner, new ThemeApplicator());
            dialog.Show(); // a Window's visual tree exists only after it is shown
            dialog.UpdateLayout(); // force the synchronous layout pass before walking the tree
            var editors = dialog.FindVisualChildren<ColorPickerEditor>().ToList();
            Assert.AreEqual(ThemeSettings.StringProperties.Count, editors.Count);
            return null;
        });

    [TestMethod]
    public void InvalidHex_DisablesApply()
        => Host.Run<object?>(() =>
        {
            var owner = new Window();
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            var dialog = new ThemeDialog(owner, new ThemeApplicator());
            dialog.Show(); // a Window's visual tree exists only after it is shown
            dialog.UpdateLayout(); // force the synchronous layout pass before walking the tree
            var editor = dialog.FindVisualChildren<ColorPickerEditor>().First();
            editor.HexBox.Text = "zzz";
            Assert.IsFalse(dialog.ApplyIsEnabledForTest);
            return null;
        });

    [TestMethod]
    public void ValidHex_EnablesApply()
        => Host.Run<object?>(() =>
        {
            var owner = new Window();
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            var dialog = new ThemeDialog(owner, new ThemeApplicator());
            dialog.Show(); // a Window's visual tree exists only after it is shown
            dialog.UpdateLayout(); // force the synchronous layout pass before walking the tree
            var editor = dialog.FindVisualChildren<ColorPickerEditor>().First();
            editor.HexBox.Text = "#F59E0B";
            Assert.IsTrue(dialog.ApplyIsEnabledForTest);
            return null;
        });

    [TestMethod]
    public void Reset_RestoresDefaultsAndReEnablesApply()
        => Host.Run<object?>(() =>
        {
            var owner = new Window();
            owner.Show(); // WPF requires a shown owner before another window can take it as Owner
            var dialog = new ThemeDialog(owner, new ThemeApplicator());
            dialog.Show(); // a Window's visual tree exists only after it is shown
            dialog.UpdateLayout(); // force the synchronous layout pass before walking the tree

            var editor = dialog.FindVisualChildren<ColorPickerEditor>().First();
            editor.HexBox.Text = "zzz";
            Assert.IsFalse(dialog.ApplyIsEnabledForTest);

            var reset = dialog.FindVisualChildren<Button>().First(b => b.Content as string == "Reset");
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.IsTrue(dialog.ApplyIsEnabledForTest);
            var defaults = new ThemeSettings();
            var props = ThemeSettings.StringProperties
                .OrderBy(p => ThemeSettings.Groups.TryGetValue(p.Name, out var group) ? group : p.Name)
                .ThenBy(p => p.Name)
                .ToList();
            var editors = dialog.FindVisualChildren<ColorPickerEditor>().ToList();
            for (int i = 0; i < editors.Count; i++)
            {
                string expected = (string?)defaults.GetType().GetProperty(props[i].Name)?.GetValue(defaults) ?? "#000000";
                Assert.AreEqual(expected, editors[i].Hex);
            }
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
