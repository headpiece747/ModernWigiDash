using System.Collections.Frozen;
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
    /// <summary>Converts a theme color to a WPF Color — the single conversion
    /// site (the resource application and the window's shadow re-application
    /// both use it, so the channels can never disagree).</summary>
    public static Color ToMediaColor(RgbaColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    /// <summary>
    /// Maps theme property names whose brush resource key does not equal the property name.
    /// All other properties map to a brush under the same name (TextPrimary maps
    /// to "TextPrimary" only — the AccentBlue alias lives in App.xaml, which the
    /// theme colors flow into through TextPrimaryColor).
    /// </summary>
    private static readonly FrozenDictionary<string, string[]> BrushKeyMap = new Dictionary<string, string[]>
    {
        ["Border"] = ["BorderBrush"],
        ["TitleBar"] = ["TitleBarBrush"]
    }.ToFrozenDictionary();

    public static void ApplyToApplication()
    {
        var resources = Application.Current?.Resources;
        if (resources == null) return;

        foreach (var prop in ThemeSettings.StringProperties)
        {
            string? hex = (string?)prop.GetValue(ThemeSettings.Theme);
            if (string.IsNullOrWhiteSpace(hex)) continue;

            var rgba = ThemeSettings.ParseColor(hex);
            if (rgba == null) continue;

            Color color = ToMediaColor(rgba.Value);
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
