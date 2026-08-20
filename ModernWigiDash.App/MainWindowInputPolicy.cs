using System.Windows;
using System.Windows.Input;

namespace ModernWigiDash.App;

/// <summary>
/// The main window's input predicates, pure so the geometry rules are
/// assertable without a UI tree: the Delete/Back-key focus guard and the
/// click-outside-deselect rule.
/// </summary>
internal static class MainWindowInputPolicy
{
    /// <summary>
    /// Whether a key press should delete the selected widget. Only Delete
    /// deletes — Backspace never does, because typing in a field that
    /// momentarily lost focus (e.g. an inspector rebuild) would otherwise
    /// nuke the selection while the user corrects text. Delete is also
    /// suppressed while a text box owns focus (it edits the field instead).
    /// </summary>
    public static bool ShouldHandleDeleteKey(Key key, bool focusIsTextBox)
        => key == Key.Delete && !focusIsTextBox;

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
