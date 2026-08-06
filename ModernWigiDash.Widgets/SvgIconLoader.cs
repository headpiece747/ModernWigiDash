using System.Collections.Concurrent;
using System.Xml.Linq;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

public static class SvgIconLoader
{
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
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return false;

        path = TextRenderHelper.SvgPathCache.GetOrParse(fullPath, () =>
            TryExtractSinglePathData(fullPath, out string? pathData) && !string.IsNullOrWhiteSpace(pathData)
                ? pathData
                : "");

        return !path.IsEmpty;
    }

    public static void Draw(SKCanvas canvas, SKPath path, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)
    {
        TextRenderHelper.DrawPathScaled(canvas, path, center, sizePx, color, offsetX, offsetY);
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
