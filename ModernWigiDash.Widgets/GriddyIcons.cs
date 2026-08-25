namespace ModernWigiDash.Widgets;

/// <summary>
/// Access to the bundled Griddy Icons (MIT) SVG path set. Icons are drawn as Skia
/// paths scaled from their native 24x24 viewBox, colored with the widget's icon color.
/// </summary>
public static class GriddyIcons
{
    // The underlying dictionary is immutable (IReadOnlyDictionary); its key
    // collection is exposed without copying and is read-only to callers.
    /// <summary>The bundled icon names (the key set, read-only, no copy).</summary>
    public static IReadOnlyCollection<string> Names => (IReadOnlyCollection<string>)GriddyIconPaths.Map.Keys;

    /// <summary>
    /// Whether the bundled set has an icon under the given name (trimmed,
    /// blank = no).
    /// </summary>
    /// <param name="name">The icon name to look up.</param>
    /// <returns>True when the icon exists.</returns>
    public static bool Contains(string name)
        => !string.IsNullOrWhiteSpace(name) && GriddyIconPaths.Map.ContainsKey(name.Trim());

    /// <summary>
    /// The raw SVG path data for the given icon name (trimmed), or false
    /// when the name is blank or unknown.
    /// </summary>
    /// <param name="name">The icon name to look up.</param>
    /// <param name="pathData">The path data when found, else empty.</param>
    /// <returns>True when the icon exists.</returns>
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

    /// <summary>
    /// The parsed SKPath for the given icon name (cached in the shared parse
    /// cache), or false when the name is unknown or the path is empty.
    /// </summary>
    /// <param name="name">The icon name to look up.</param>
    /// <param name="path">The parsed path when found, else null.</param>
    /// <returns>True when a non-empty path was produced.</returns>
    public static bool TryGetPath(string name, out SKPath? path)
    {
        path = null;
        if (!TryGetPathData(name, out string pathData)) return false;

        path = SvgIconHelper.SvgPathCache.GetOrParse(name.Trim(), pathData);
        return !path.IsEmpty;
    }

    /// <summary>
    /// Draws the named bundled icon centered at the point at the given size
    /// (scaled from its 24x24 viewBox), no-op when the size is non-positive
    /// or the icon is unknown.
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="name">The icon name to draw.</param>
    /// <param name="center">The icon's center in canvas coordinates.</param>
    /// <param name="sizePx">The icon's side length in px.</param>
    /// <param name="color">The icon color.</param>
    /// <param name="offsetX">Horizontal offset in px.</param>
    /// <param name="offsetY">Vertical offset in px.</param>
    public static void Draw(SKCanvas canvas, string name, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)
    {
        if (sizePx <= 0) return;
        if (!TryGetPath(name, out SKPath? path) || path == null || path.IsEmpty) return;

        SvgIconHelper.DrawPathScaled(canvas, path, center, sizePx, color, offsetX, offsetY);
    }
}
