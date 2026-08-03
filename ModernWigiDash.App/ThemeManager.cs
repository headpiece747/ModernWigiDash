using System.Reflection;
using System.Windows;
using System.Windows.Media;
using ModernWigiDash.Core.Theming;

namespace ModernWigiDash.App;

/// <summary>
/// Pushes the WPF-free <see cref="ThemeSettings"/> into the WPF application resources.
/// Color values land under the key <c>{propertyName}Color</c>; brushes are rebuilt and
/// reassigned under their <c>{propertyName}</c> keys so every DynamicResource consumer
/// repaints immediately — no reliance on a Color-to-brush freezable chain.
/// </summary>
public static class ThemeManager
{
    /// <summary>
    /// Maps theme property names whose brush resource key does not equal the property name.
    /// All other properties map to a brush under the same name.
    /// </summary>
    private static readonly Dictionary<string, string[]> BrushKeyMap = new()
    {
        ["Border"] = new[] { "BorderBrush" },
        ["TextPrimary"] = new[] { "TextPrimary", "AccentBlue" },
        ["TitleBar"] = new[] { "TitleBarBrush" }
    };

    public static void ApplyToApplication()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        foreach (var prop in typeof(ThemeSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType != typeof(string)) continue;
            string? hex = (string?)prop.GetValue(ThemeSettings.Theme);
            if (string.IsNullOrWhiteSpace(hex)) continue;

            var rgba = ThemeSettings.ParseColor(hex);
            if (rgba == null) continue;

            Color color = Color.FromArgb(rgba.Value.A, rgba.Value.R, rgba.Value.G, rgba.Value.B);
            resources[$"{prop.Name}Color"] = color;

            var brush = new SolidColorBrush(color);
            if (BrushKeyMap.TryGetValue(prop.Name, out var brushKeys))
            {
                foreach (var key in brushKeys) resources[key] = brush;
            }
            else
            {
                resources[prop.Name] = brush;
            }
        }
    }
}
