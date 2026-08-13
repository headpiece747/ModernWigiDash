using System.Collections.Concurrent;
using System.Windows.Media;
using ModernWigiDash.Widgets;

namespace ModernWigiDash.App.Update;

/// <summary>
/// WPF geometry for the bundled Griddy icon paths: parses the SVG path data
/// from <see cref="GriddyIconPaths.Map"/> via <see cref="Geometry.Parse"/> and
/// caches per name — one parse per icon, shared by every header button.
/// </summary>
public static class GriddyIconGeometry
{
    private static readonly ConcurrentDictionary<string, Geometry?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parsed geometry for <paramref name="name"/>, or null when unknown.</summary>
    public static Geometry? FromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return Cache.GetOrAdd(name.Trim(), static key =>
            GriddyIconPaths.Map.TryGetValue(key, out string? pathData)
                ? ParsePathData(pathData)
                : null);
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

    internal static int CacheCount => Cache.Count;
}
