
namespace ModernWigiDash.Widgets;

/// <summary>
/// Access to the bundled Griddy Icons (MIT) SVG path set. Icons are drawn as Skia
/// paths scaled from their native 24x24 viewBox, colored with the widget's icon color.
/// </summary>
public static class GriddyIcons
{
    // The underlying dictionary is immutable (IReadOnlyDictionary); its key
    // collection is exposed without copying and is read-only to callers.
    public static IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)GriddyIconPaths.Map.Keys;

    public static bool Contains(string name)
        => !string.IsNullOrWhiteSpace(name) && GriddyIconPaths.Map.ContainsKey(name.Trim());

    public static bool TryGetPathData(string name, out string pathData)
    {
        pathData = "";
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (GriddyIconPaths.Map.TryGetValue(name.Trim(), out string? found))
        {
            pathData = found;
            return true;
        }
        return false;
    }

    public static bool TryGetPath(string name, out SKPath? path)
    {
        path = null;
        if (!TryGetPathData(name, out string pathData)) return false;

        path = SvgIconHelper.SvgPathCache.GetOrParse(name.Trim(), pathData);
        return !path.IsEmpty;
    }

    public static void Draw(SKCanvas canvas, string name, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)
    {
        if (sizePx <= 0) return;
        if (!TryGetPath(name, out SKPath? path) || path == null || path.IsEmpty) return;

        SvgIconHelper.DrawPathScaled(canvas, path, center, sizePx, color, offsetX, offsetY);
    }
}
