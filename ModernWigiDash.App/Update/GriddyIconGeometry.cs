using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Update;

/// <summary>
/// WPF geometry for the bundled Griddy icon paths: parses the SVG path data
/// from <see cref="GriddyIconPaths.Map"/> via <see cref="Geometry.Parse"/> and
/// caches per name — one parse per icon, shared by every header button. The
/// cache rule is the shared Sdk <see cref="SvgPathParseCache{T}"/>; the
/// WPF-specific part is only the parser and the null fallback.
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
