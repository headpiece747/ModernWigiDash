using System.Reflection;
using ModernWigiDash.Core.Models;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Pure reflection→model mapping: turns a widget's <c>[WidgetProperty]</c>
/// attributes into <see cref="EditorDescription"/>s. No WPF, no dialogs, no
/// window state — the interface is the test surface. A thin WPF renderer
/// (<see cref="InspectorPanelRenderer"/>) consumes the descriptions, and every
/// write-back funnels through the host's single
/// <c>ApplyInspectorPropertyValue</c> seam.
/// </summary>
public static class InspectorModelBuilder
{
    /// <summary>
    /// Describes every editable property on the widget's active instance, in
    /// declaration order. Skips properties that never get their own row
    /// (e.g. the Hotkey <c>IconFile</c> companion, written by the icon picker).
    /// </summary>
    public static IReadOnlyList<EditorDescription> Describe(PlacedWidgetInstance widget)
    {
        var instance = widget.ActiveInstance;
        if (instance == null) return [];

        List<EditorDescription> result = [];
        var type = instance.GetType();

        foreach (var prop in type.GetProperties())
        {
            var attr = prop.GetCustomAttribute<WidgetPropertyAttribute>();
            if (attr == null) continue;

            // IconFile is a hidden companion of the Icon editor (the picker
            // writes it through the browse seam); it never gets its own row.
            // Widgets declare this via IWidgetEditorProvider — no widget-type
            // branches here.
            if (instance is IWidgetEditorProvider editorProvider &&
                editorProvider.GetEditorKind(prop) == EditorKind.IconPicker)
                continue;

            result.Add(new EditorDescription(
                prop,
                attr.DisplayName,
                attr.PropertyType,
                prop.GetValue(instance) ?? attr.DefaultValue,
                ResolveOptions(instance, prop, attr),
                attr.PropertyType == WidgetPropertyType.Button));
        }

        return result;
    }

    private static IReadOnlyList<WidgetPropertyOption> ResolveOptions(
        IModernWidget instance, PropertyInfo prop, WidgetPropertyAttribute attr)
    {
        if (instance is IWidgetPropertyOptionsProvider provider)
        {
            var dynamic = provider.GetPropertyOptions(prop.Name);
            if (dynamic.Count > 0) return dynamic;
        }

        if (attr.PropertyType == WidgetPropertyType.SensorSelector)
        {
            // Live sensor labels from the store — pure data, no UI dependency.
            return LhmSensorStore.ReadSnapshot()
                .Readings
                .Select(r => new WidgetPropertyOption(r.Label, r.Label))
                .Distinct()
                .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return attr.Options
            .Select(option => new WidgetPropertyOption(option, option))
            .ToArray();
    }
}
