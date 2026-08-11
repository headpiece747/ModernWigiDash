using System.Windows;

namespace ModernWigiDash.App;

/// <summary>
/// The main window's input predicates, pure so the geometry rules are
/// assertable without a UI tree: the Delete/Back-key focus guard and the
/// click-outside-deselect rule (both previously inline in MainWindow.xaml.cs).
/// </summary>
internal static class MainWindowInputPolicy
{
    /// <summary>
    /// Whether Delete/Back should delete the selected widget: never while a
    /// text box owns focus — Backspace must edit the field, not delete.
    /// </summary>
    public static bool ShouldHandleDeleteKey(bool focusIsTextBox) => !focusIsTextBox;

    /// <summary>
    /// Whether a mouse-down outside both panels should deselect the selected
    /// widget: true only when the click lies outside the canvas AND outside
    /// the inspector. Bounds are inclusive — a click exactly on an edge still
    /// counts as inside its panel.
    /// </summary>
    public static bool ShouldDeselect(
        Point canvasPos, Size canvasSize,
        Point inspectorPos, Size inspectorSize)
        => !IsInside(canvasPos, canvasSize) && !IsInside(inspectorPos, inspectorSize);

    private static bool IsInside(Point pos, Size size)
        => pos.X >= 0 && pos.Y >= 0 && pos.X <= size.Width && pos.Y <= size.Height;
}
