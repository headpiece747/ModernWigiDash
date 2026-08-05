using System.Collections.Concurrent;
using System.Xml.Linq;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

internal static class SvgIconLoader
{
    private static readonly ConcurrentDictionary<string, SKPath> PathCache = new(StringComparer.OrdinalIgnoreCase);

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

        path = PathCache.GetOrAdd(fullPath, key =>
        {
            try
            {
                if (!TryExtractSinglePathData(key, out string? pathData) || string.IsNullOrWhiteSpace(pathData))
                    return new SKPath();

                SKPath? parsed = SKPath.ParseSvgPathData(pathData);
                if (parsed != null && parsed.Bounds.Width > 0 && parsed.Bounds.Height > 0)
                {
                    parsed.FillType = SKPathFillType.Winding;
                    return parsed;
                }
                parsed?.Dispose();
            }
            catch
            {
            }
            return new SKPath();
        });

        return !path.IsEmpty;
    }

    public static void Draw(SKCanvas canvas, SKPath path, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)
    {
        if (sizePx <= 0 || path.IsEmpty) return;
        var bounds = path.Bounds;
        float maxDim = Math.Max(bounds.Width, bounds.Height);
        if (maxDim <= 0) return;

        float scale = sizePx / maxDim;
        canvas.Save();
        canvas.Translate(center.X + offsetX, center.Y + offsetY);
        canvas.Scale(scale, scale);
        canvas.Translate(-bounds.MidX, -bounds.MidY);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawPath(path, paint);
        canvas.Restore();
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
