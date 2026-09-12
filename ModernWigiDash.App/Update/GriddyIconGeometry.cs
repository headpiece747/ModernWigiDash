using System.Windows.Media;
using System.Windows.Shapes;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Update;

/// <summary>
/// WPF geometry for the bundled Griddy icon paths: parses the SVG path data
/// from <see cref="GriddyIconPaths.Map"/> via <see cref="Geometry.Parse"/> and
/// caches per name — one parse per icon, shared by every header button and the
/// inspector's icon picker. The cache rule is the shared Sdk
/// <see cref="SvgPathParseCache{T}"/>; the WPF-specific part is only the parser,
/// the null fallback, and the 22x22 white cell wrapper (<see cref="BuildCell"/>).
/// </summary>
internal static class GriddyIconGeometry
{
    /// <summary>Parsed geometry for <paramref name="name"/>, or null when unknown.</summary>
    public static Geometry? FromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string key = name.Trim();
        if (!GriddyIconPaths.Map.TryGetValue(key, out string? pathData)) return null;
        return SvgPathParseCache<Geometry>.GetOrParse(key, () => ParsePathData(pathData));
    }

    /// <summary>
    /// The one owner of "named Griddy icon to a drawable WPF cell": resolves
    /// the cached geometry (<see cref="FromName"/>) and wraps it in the 22x22
    /// white glyph the picker grid draws, returning null for an unknown or
    /// malformed name (an empty cell, never a crash). Both call sites route
    /// through this so the parse + cache + fallback rule has a single spelling.
    /// </summary>
    public static Path? BuildCell(string name)
    {
        Geometry? geometry = FromName(name);
        if (geometry is null) return null;
        return new Path
        {
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            Fill = Brushes.White,
            Data = geometry
        };
    }

    internal static Geometry? ParsePathData(string pathData)
    {
        try
        {
            return string.IsNullOrWhiteSpace(pathData) ? null : Geometry.Parse(pathData);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
