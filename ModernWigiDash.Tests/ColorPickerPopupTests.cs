using System.Windows;
using System.Windows.Controls.Primitives;
using ModernWigiDash.App.Controls;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.Tests;

[TestClass]
public class ColorPickerPopupTests
{
    private static readonly StaHost Host = new("ColorPickerPopupTests-STA");

    [TestCleanup]
    public void Cleanup() => Host.DetachApplication();

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
            popup.ApplyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.AreEqual("#F59E0B", applied);
        });

    [TestMethod]
    public void Cancel_RaisesCancelled()
        => StaRunner.Run(() =>
        {
            var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
            bool cancelled = false;
            popup.Cancelled += () => cancelled = true;
            popup.CancelButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.IsTrue(cancelled);
        });

    [TestMethod]
    public void Presets_ArePopulatedFromPalette()
        => StaRunner.Run(() =>
        {
            var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
            Assert.AreEqual(PresetPalette.Swatches.Count, popup.PresetPanel.Children.Count);
        });

    /// <summary>
    /// The SV square must be an input hit-test target so the thumb can be
    /// dragged. All its children are IsHitTestVisible=false (the overlays and
    /// the thumb must not swallow drags), so the canvas itself needs a
    /// non-null Background — a panel with a null Background is invisible to
    /// WPF hit testing. Requires a shown window (InputHitTest needs a
    /// PresentationSource; an unrooted element reports IsVisible=false).
    /// </summary>
    [TestMethod]
    public void SvSquare_IsInputHitTestTarget()
        => Host.Run<object?>(() =>
        {
            var window = new Window();
            try
            {
                var popup = new ColorPickerPopup(new RgbaColor(255, 245, 158, 11));
                window.Content = popup;
                window.Show();
                window.UpdateLayout();

                var hit = popup.SvCanvas.InputHitTest(new Point(126, 65));
                Assert.AreEqual(popup.SvCanvas, hit,
                    "A click in the SV square's center must hit the canvas so the drag handlers fire");
                return null;
            }
            finally
            {
                window.Close();
            }
        });
}
