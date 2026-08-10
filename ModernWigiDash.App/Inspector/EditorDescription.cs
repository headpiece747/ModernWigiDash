using System.Reflection;
using ModernWigiDash.Sdk;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Pure description of one <c>[WidgetProperty]</c> editor — what the inspector
/// must know to render and write back a property, with no WPF in sight.
/// </summary>
/// <param name="Property">The reflected property being edited.</param>
/// <param name="DisplayName">Label shown above the editor.</param>
/// <param name="PropertyType">Which editor shape to render.</param>
/// <param name="CurrentValue">The property's current value (or the attribute default).</param>
/// <param name="Options">Choice options: attribute <c>Options</c>, the widget's
/// <see cref="IWidgetPropertyOptionsProvider"/> list, or the live sensor list
/// for <see cref="WidgetPropertyType.SensorSelector"/>.</param>
/// <param name="IsAction">True for <see cref="WidgetPropertyType.Button"/> —
/// an executable action, not a value editor.</param>
public sealed record EditorDescription(
    PropertyInfo Property,
    string DisplayName,
    WidgetPropertyType PropertyType,
    object? CurrentValue,
    IReadOnlyList<WidgetPropertyOption> Options,
    bool IsAction);
