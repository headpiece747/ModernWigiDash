using System.Collections.Generic;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Registry of custom property editors. Provides pluggable lookup by <see cref="EditorKind"/>,
/// allowing custom widget property editors to be added or tested modularly without
/// modifying <see cref="InspectorPanelRenderer"/>.
/// </summary>
internal sealed class InspectorEditorRegistry
{
    private readonly Dictionary<EditorKind, ICustomPropertyEditor> _editors = new();

    /// <summary>The shared default editor registry with built-in editors registered.</summary>
    public static InspectorEditorRegistry Default { get; } = CreateDefault();

    /// <summary>Registers an editor builder for its declared <see cref="ICustomPropertyEditor.Kind"/>.</summary>
    public void Register(ICustomPropertyEditor editor)
    {
        _editors[editor.Kind] = editor;
    }

    /// <summary>Tries to get a registered custom editor for the given kind.</summary>
    public bool TryGetEditor(EditorKind kind, out ICustomPropertyEditor? editor)
    {
        return _editors.TryGetValue(kind, out editor);
    }

    /// <summary>Creates a default registry pre-loaded with standard editors.</summary>
    public static InspectorEditorRegistry CreateDefault()
    {
        var registry = new InspectorEditorRegistry();
        registry.Register(new CalendarFeedPropertyEditor());
        registry.Register(new TodoManagerPropertyEditor());
        registry.Register(new LocationSearchPropertyEditor());
        registry.Register(new KeyCapturePropertyEditor());
        return registry;
    }
}
