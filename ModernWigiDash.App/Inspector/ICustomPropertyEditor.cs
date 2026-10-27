using System.Windows;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Pluggable seam for custom widget property editors in the inspector panel.
/// Implementations build the WPF visual editor tree for a specific <see cref="EditorKind"/>.
/// </summary>
internal interface ICustomPropertyEditor
{
    /// <summary>The <see cref="EditorKind"/> handled by this editor builder.</summary>
    EditorKind Kind { get; }

    /// <summary>
    /// Builds the editor UI element for the given property description.
    /// </summary>
    UIElement BuildEditor(PlacedWidgetInstance widget, EditorDescription desc, InspectorCallbacks callbacks);
}
