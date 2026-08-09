using System.Reflection;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Editor kind for a property, or null when the generic editor suffices.
/// </summary>
public enum EditorKind
{
    IconPicker,
    ActionCommand
}

/// <summary>
/// Optional widget capability: supplies special inspector editors for its own
/// properties (e.g. the hotkey widget's icon picker and action-command editor).
/// The inspector renderer asks the widget instead of branching on widget types.
/// </summary>
public interface IWidgetEditorProvider
{
    /// <summary>Editor kind for a property, or null when the generic editor suffices.</summary>
    EditorKind? GetEditorKind(PropertyInfo property);

    /// <summary>
    /// Companion property written alongside an icon-picker editor (e.g. the
    /// file-path property that overrides the named icon), or null when none.
    /// The picker and the typed-in editor both clear it when the named icon
    /// changes, so the two icon sources can never disagree.
    /// </summary>
    PropertyInfo? GetIconFileCompanion(PropertyInfo iconProperty) => null;

    /// <summary>
    /// Name of the choice property whose selected value toggles the
    /// action-command editor's visibility, or null when the widget has no
    /// such pairing (the action-command editor is always visible).
    /// </summary>
    string? ActionCommandVisibilityChoicePropertyName => null;

    /// <summary>
    /// Whether the action-command editor should be visible for the given
    /// selected choice value. Only consulted when
    /// <see cref="ActionCommandVisibilityChoicePropertyName"/> is set.
    /// </summary>
    bool IsActionCommandVisible(string? actionTypeValue) => true;
}
