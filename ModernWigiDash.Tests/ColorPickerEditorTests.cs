using System.Windows.Media;
using ModernWigiDash.App.Controls;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

[TestClass]
public class ColorPickerEditorTests
{
    [TestMethod]
    public void HexSetter_UpdatesSwatchBrush()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor();
            editor.Hex = "#F59E0B";
            var brush = editor.SwatchButton.Background as SolidColorBrush;
            Assert.AreEqual(Color.FromRgb(245, 158, 11), brush!.Color);
        });

    [TestMethod]
    public void ValidHexTextChange_RaisesApplied()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor();
            string? applied = null;
            editor.Applied += hex => applied = hex;
            editor.HexBox.Text = "#00FF00";
            Assert.AreEqual("#00FF00", applied);
        });

    [TestMethod]
    public void InvalidHexText_DoesNotRaiseApplied_AndFlagsInvalid()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor();
            bool applied = false;
            int changed = 0;
            editor.Applied += _ => applied = true;
            editor.Changed += () => changed++;
            editor.HexBox.Text = "not-a-color";
            Assert.IsFalse(applied);
            Assert.IsFalse(editor.IsValidHex);
            Assert.AreEqual(1, changed);
        });

    [TestMethod]
    public void HexSetter_RaisesNeitherAppliedNorChanged()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor();
            bool applied = false;
            bool changed = false;
            editor.Applied += _ => applied = true;
            editor.Changed += () => changed = true;
            editor.Hex = "#00FF00";
            Assert.IsFalse(applied);
            Assert.IsFalse(changed);
        });

    [TestMethod]
    public void PopupApply_RaisesChanged()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor();
            int changed = 0;
            editor.Changed += () => changed++;
            editor.PopupContent.ApplyButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.AreEqual(1, changed);
        });

    [TestMethod]
    public void ShowHexBoxFalse_HidesHexBox()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor { ShowHexBox = false };
            Assert.AreEqual(System.Windows.Visibility.Collapsed, editor.HexBox.Visibility);
        });

    [TestMethod]
    public void PopupApply_RaisesApplied_WithPopupHex()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor { Hex = "#F59E0B" };
            string? applied = null;
            editor.Applied += hex => applied = hex;
            editor.PopupContent.ApplyButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.AreEqual("#F59E0B", applied);
        });

    [TestMethod]
    public void PopupApply_WithDifferentPopupColor_UpdatesHexAndSwatch()
        => StaRunner.Run(() =>
        {
            var editor = new ColorPickerEditor { Hex = "#F59E0B" };
            editor.PopupContent.SetFromHex("#00FF00");
            editor.PopupContent.ApplyButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.AreEqual("#00FF00", editor.Hex);
            var brush = editor.SwatchButton.Background as SolidColorBrush;
            Assert.AreEqual(Color.FromRgb(0, 255, 0), brush!.Color);
        });
}
