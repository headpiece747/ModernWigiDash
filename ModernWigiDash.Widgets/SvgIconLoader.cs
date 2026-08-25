using System.Collections.Concurrent;
using System.Xml.Linq;

namespace ModernWigiDash.Widgets;

public static class SvgIconLoader
{
    // File-existence probes are cached per path — positive and negative alike:
    // Render hit-tests the icon geometry every frame, and File.Exists per
    // frame is a filesystem hit. The parsed path is cached in SvgPathCache
    // anyway, so the probe result is stable for the process lifetime of a
    // given path. CopyToIcons (the one runtime path that adds icon files)
    // refreshes the entry, so a copied icon appears on the next frame.
    private static readonly ConcurrentDictionary<string, bool> ExistenceCache = new(StringComparer.OrdinalIgnoreCase);

    public static string IconsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernWigiDash",
        "icons");

    public static string ResolveFullPath(string iconFile)
    {
        if (string.IsNullOrWhiteSpace(iconFile)) return "";
        return Path.IsPathRooted(iconFile)
            ? iconFile
            : Path.Combine(IconsDirectory, iconFile);
    }

    public static string CopyToIcons(string sourcePath)
    {
        Directory.CreateDirectory(IconsDirectory);
        string fileName = $"{Path.GetFileNameWithoutExtension(sourcePath)}_{Guid.NewGuid():N}.svg";
        string destination = Path.Combine(IconsDirectory, fileName);
        File.Copy(sourcePath, destination);
        ExistenceCache[destination] = true;
        return fileName;
    }

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
