using System.Globalization;

using System.ComponentModel;
using System.Reflection;
using ModernWigiDash.Core.Rendering;

namespace ModernWigiDash.App.Inspector;

/// <summary>
/// Pure inspector value policy: string→typed conversion, transform parsing
/// (min-size guard, rotation normalization), opacity clamping, and value
/// formatting. No WPF or control references — every rule is testable without
/// a window. The controller binds its controls to this policy and stays a
/// thin binder.
/// </summary>
internal sealed class InspectorValuePolicy
{
    /// <summary>Minimum width/height a widget may be resized to via the inspector —
    /// a distinct policy from InputController's drag-resize floors (handles
    /// stay grabbable); both floors live in <see cref="WidgetSizeLimits"/>, so
    /// they can never drift apart.</summary>
    private static readonly float MinWidgetSize = WidgetSizeLimits.MinInspectorSize;

    /// <summary>
    /// Warning sink for conversion/parse failures. The policy has no logging
    /// seam by design (pure, WPF-free); the controller wires this to the
    /// shared file log. Defaults to debug output.
    /// </summary>
    internal Action<string>? LogWarning { get; set; } = msg => System.Diagnostics.Debug.WriteLine(msg);

    /// <summary>
    /// Converts inspector text into the property's CLR type. String properties
    /// pass through unchanged; unconvertible text returns false and logs a
    /// diagnostic. Backed by <see cref="TypeDescriptor"/>'s invariant-string
    /// converters so a Number/Color/etc. property is never silently dropped
    /// by a SetValue type mismatch.
    /// </summary>
    public bool TryConvertStringToType(PropertyInfo property, string text, out object? value)
    {
        if (property.PropertyType == typeof(string))
        {
            value = text;
            return true;
        }

        try
        {
            value = TypeDescriptor.GetConverter(property.PropertyType).ConvertFromInvariantString(text);
            return true;
        }
        catch (Exception ex)
        {
            LogWarning?.Invoke($"Inspector value '{text}' not convertible to {property.PropertyType.Name} for {property.Name}: {ex.Message}");
            value = null;
            return false;
        }
    }

    /// <summary>Parses a position field (X/Y).</summary>
    public bool TryParsePosition(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>Parses a size field (Width/Height); values at or below the minimum are rejected.</summary>
    public bool TryParseSize(string text, out float value)
        => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > MinWidgetSize;

    /// <summary>Parses a ZIndex field.</summary>
    public bool TryParseZIndex(string text, out int value) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Parses a rotation field in degrees, normalized to its 360° remainder
    /// (negative input keeps a negative remainder, matching the display's
    /// rotation model).
    /// </summary>
    public bool TryParseRotation(string text, out float value)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        value %= 360;
        return true;
    }

    /// <summary>Clamps an opacity to the displayable 0..1 range.</summary>
    public float ClampOpacity(float opacity) => Math.Clamp(opacity, 0f, 1f);

    /// <summary>Formats a transform field (X/Y/Width/Height/Rotation) as a whole number.</summary>
    public string FormatTransformValue(float value) => $"{value:F0}";

    /// <summary>Formats an opacity as a percentage label (truncated, as the slider displays it).</summary>
    public string FormatOpacityPercent(float opacity) => $"{(int)(opacity * 100)}%";

    /// <summary>Formats an arbitrary property value for display; null renders as empty.</summary>
    public string FormatValue(object? value) => value?.ToString() ?? "";
}
