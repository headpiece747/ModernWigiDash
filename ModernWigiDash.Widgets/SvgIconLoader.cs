using System.Collections.Concurrent;
using System.Xml.Linq;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Loads custom SVG icon files from the icons folder for the widget's icon
/// properties: resolves paths, copies files in, parses a single-path SVG to
/// an SKPath (existence and parse both cached), and draws it scaled.
/// </summary>
public static class SvgIconLoader
{
    // File-existence probes are cached per path — positive and negative alike:
    // Render hit-tests the icon geometry every frame, and File.Exists per
    // frame is a filesystem hit. The parsed path is cached in SvgPathCache
    // anyway, so the probe result is stable for the process lifetime of a
    // given path. CopyToIcons (the one runtime path that adds icon files)
    // refreshes the entry, so a copied icon appears on the next frame.
    private static readonly ConcurrentDictionary<string, bool> ExistenceCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The per-user icons directory under LocalApplicationData.</summary>
    public static string IconsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernWigiDash",
        "icons");

    /// <summary>
    /// The full path for an icon file value: rooted paths pass through,
    /// relative ones resolve under the icons directory (blank = empty).
    /// </summary>
    /// <param name="iconFile">The icon file value (rooted or relative).</param>
    /// <returns>The full path, or empty when the value is blank.</returns>
    public static string ResolveFullPath(string iconFile)
    {
        if (string.IsNullOrWhiteSpace(iconFile)) return "";
        return Path.IsPathRooted(iconFile)
            ? iconFile
            : Path.Combine(IconsDirectory, iconFile);
    }

    /// <summary>
    /// Copies the source SVG into the icons directory under a unique name
    /// and refreshes the existence cache so the copy appears on the next
    /// frame.
    /// </summary>
    /// <param name="sourcePath">The source SVG file to copy.</param>
    /// <returns>The new file name in the icons directory.</returns>
    public static string CopyToIcons(string sourcePath)
    {
        Directory.CreateDirectory(IconsDirectory);
        string fileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}_{Guid.NewGuid():N}.svg";
        string destination = Path.Combine(IconsDirectory, fileName);
        File.Copy(sourcePath, destination);
        ExistenceCache[destination] = true;
        return fileName;
    }

    /// <summary>
    /// The parsed SKPath for an icon file (existence cached per path, the
    /// path in the shared parse cache), or false when the file is missing or
    /// has no single parseable path.
    /// </summary>
    /// <param name="iconFile">The icon file value (rooted or relative).</param>
    /// <param name="path">The parsed path when found, else null.</param>
    /// <returns>True when a non-empty path was produced.</returns>
    public static bool TryGetPath(string iconFile, out SKPath? path)
    {
        path = null;
        string fullPath = ResolveFullPath(iconFile);
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        // Existence is cached per path — a missing IconFile must not hit the
        // filesystem 30×/s. CopyToIcons refreshes the entry when it adds a
        // file, so a runtime-copied icon still appears on the next frame.
        if (!ExistenceCache.TryGetValue(fullPath, out bool exists))
        {
            exists = File.Exists(fullPath);
            ExistenceCache.TryAdd(fullPath, exists);
        }
        if (!exists) return false;

        path = SvgIconHelper.SvgPathCache.GetOrParse(fullPath, () =>
            TryExtractSinglePathData(fullPath, out string? pathData) && !string.IsNullOrWhiteSpace(pathData)
                ? pathData
                : "");

        return !path.IsEmpty;
    }

    /// <summary>
    /// Draws the parsed icon path centered at the point, scaled to the given
    /// size (the draw-scaling protocol lives in SvgIconHelper).
    /// </summary>
    /// <param name="canvas">The canvas to draw on.</param>
    /// <param name="path">The parsed icon path.</param>
    /// <param name="center">The icon's center in canvas coordinates.</param>
    /// <param name="sizePx">The icon's side length in px.</param>
    /// <param name="color">The icon color.</param>
    /// <param name="offsetX">Horizontal offset in px.</param>
    /// <param name="offsetY">Vertical offset in px.</param>
    public static void Draw(SKCanvas canvas, SKPath path, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)
    {
        SvgIconHelper.DrawPathScaled(canvas, path, center, sizePx, color, offsetX, offsetY);
    }

    private static bool TryExtractSinglePathData(string filePath, out string? pathData)
    {
        pathData = null;
        XDocument doc;
        try
        {
            doc = XDocument.Load(filePath);
        }
        catch (Exception)
        {
            // A malformed SVG (invalid XML, or an I/O failure on the file)
            // is a no-icon, not a render-tick crash: the path passed the
            // existence check but does not parse.
            return false;
        }
        var paths = doc.Descendants().Where(e => string.Equals(e.Name.LocalName, "path", StringComparison.Ordinal)).ToList();
        if (paths.Count != 1) return false;
        pathData = paths[0].Attribute("d")?.Value;
        return !string.IsNullOrWhiteSpace(pathData);
    }
}
