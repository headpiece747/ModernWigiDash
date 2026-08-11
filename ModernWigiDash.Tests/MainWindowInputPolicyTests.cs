using System.Windows;
using ModernWigiDash.App;

namespace ModernWigiDash.Tests;

/// <summary>
/// The main window's input predicates (MainWindowInputPolicy) — pure
/// geometry/focus rules, assertable without a UI tree.
/// </summary>
[TestClass]
public class MainWindowInputPolicyTests
{
    // ── Delete/Back key guard ──

    [TestMethod]
    public void ShouldHandleDeleteKey_FocusInTextBox_ReturnsFalse()
    {
        Assert.IsFalse(MainWindowInputPolicy.ShouldHandleDeleteKey(focusIsTextBox: true),
            "Backspace must edit the focused field, not delete the selected widget");
    }

    [TestMethod]
    public void ShouldHandleDeleteKey_FocusElsewhere_ReturnsTrue()
    {
        Assert.IsTrue(MainWindowInputPolicy.ShouldHandleDeleteKey(focusIsTextBox: false));
    }

    // ── click-outside-deselect ──

    [TestMethod]
    public void ShouldDeselect_PointInsideCanvas_ReturnsFalse()
    {
        Assert.IsFalse(MainWindowInputPolicy.ShouldDeselect(
            new Point(100, 100), new Size(1016, 592),
            new Point(-10, -10), new Size(400, 600)));
    }

    [TestMethod]
    public void ShouldDeselect_PointExactlyOnCanvasEdge_ReturnsFalse()
    {
        // The bounds are inclusive — a click exactly on an edge still belongs
        // to the canvas and must not deselect.
        Assert.IsFalse(MainWindowInputPolicy.ShouldDeselect(
            new Point(1016, 592), new Size(1016, 592),
            new Point(-10, -10), new Size(400, 600)));
    }

    [TestMethod]
    public void ShouldDeselect_PointOutsideCanvasButInsideInspector_ReturnsFalse()
    {
        Assert.IsFalse(MainWindowInputPolicy.ShouldDeselect(
            new Point(1017, 300), new Size(1016, 592),
            new Point(200, 300), new Size(400, 600)),
            "a click over the inspector panel must not deselect, even outside the canvas");
    }

    [TestMethod]
    public void ShouldDeselect_PointOutsideBothPanels_ReturnsTrue()
    {
        Assert.IsTrue(MainWindowInputPolicy.ShouldDeselect(
            new Point(1017, 300), new Size(1016, 592),
            new Point(200, 650), new Size(400, 600)));
    }

    [TestMethod]
    public void ShouldDeselect_NegativeCoordinatesOutsideBothPanels_ReturnsTrue()
    {
        Assert.IsTrue(MainWindowInputPolicy.ShouldDeselect(
            new Point(-5, 300), new Size(1016, 592),
            new Point(200, -5), new Size(400, 600)),
            "a click left or above a panel edge is outside it and must deselect");
    }
}
