using System.Collections.Concurrent;
using System.Xml.Linq;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

public static class SvgIconLoader
{
    // File-existence probes are cached per path: Render hit-tests the icon
    // geometry every frame, and File.Exists per frame is a filesystem hit. The
    // parsed path is cached in SvgPathCache anyway, so the probe result is
    // stable for the process lifetime of a given path.
    private static readonly ConcurrentDictionary<string, byte> ExistenceCache = new(StringComparer.OrdinalIgnoreCase);

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
        File.Copy(sourcePath, Path.Combine(IconsDirectory, fileName));
        return fileName;
    }

    public static bool TryGetPath(string iconFile, out SKPath? path)
    {
        path = null;
        string fullPath = ResolveFullPath(iconFile);
        if (string.IsNullOrWhiteSpace(fullPath)) return false;

        // Only positive results are cached: an icon copied into IconsDirectory
        // at runtime (the picker's flow) must become visible on the next frame,
        // so negatives are re-probed each call.
        if (ExistenceCache.ContainsKey(fullPath) || File.Exists(fullPath))
        {
            ExistenceCache.TryAdd(fullPath, 0);
        }
        else
        {
            return false;
        }

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
        var doc = XDocument.Load(filePath);
        var paths = doc.Descendants().Where(e => e.Name.LocalName == "path").ToList();
        if (paths.Count != 1) return false;
        pathData = paths[0].Attribute("d")?.Value;
        return !string.IsNullOrWhiteSpace(pathData);
    }
}
