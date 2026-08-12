using System.Windows.Controls;
using ModernWigiDash.App.Controls;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

[TestClass]
public class ColorPickerPopupTests
{
    [TestMethod]
    public void Ctor_InitialColor_ExposesCurrentColor()
        => StaRunner.Run(() =>
        {
            var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
            Assert.AreEqual(new RgbaColor(255, 245, 158, 11), popup.CurrentColor);
        });

    [TestMethod]
    public void Apply_RaisesApplied_WithFormattedHex()
        => StaRunner.Run(() =>
        {
            var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
            string? applied = null;
            popup.Applied += hex => applied = hex;
            popup.ApplyButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.AreEqual("#F59E0B", applied);
        });

    [TestMethod]
    public void Cancel_RaisesCancelled()
        => StaRunner.Run(() =>
        {
            var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
            bool cancelled = false;
            popup.Cancelled += () => cancelled = true;
            popup.CancelButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.IsTrue(cancelled);
        });

    [TestMethod]
    public void Presets_ArePopulatedFromPalette()
        => StaRunner.Run(() =>
        {
            var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
            Assert.AreEqual(PresetPalette.Swatches.Count, popup.PresetPanel.Children.Count);
        });
}
