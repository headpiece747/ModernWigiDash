using System.Collections.Concurrent;
using SkiaSharp;

namespace ModernWigiDash.Widgets;

/// <summary>
/// Access to the bundled Griddy Icons (MIT) SVG path set. Icons are drawn as Skia
/// paths scaled from their native 24x24 viewBox, colored with the widget's icon color.
/// </summary>
public static class GriddyIcons
{
    private static readonly ConcurrentDictionary<string, SKPath> PathCache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Names => GriddyIconPaths.Map.Keys.ToArray();

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

        path = PathCache.GetOrAdd(name.Trim(), key =>
        {
            try
            {
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
                // Fall through to an empty path.
                System.Diagnostics.Debug.WriteLine("Failed to parse SVG path data; returning empty path");
            }
            return new SKPath();
        });

        return !path.IsEmpty;
    }

    public static void Draw(SKCanvas canvas, string name, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)
    {
        if (sizePx <= 0) return;
        if (!TryGetPath(name, out SKPath? path) || path == null || path.IsEmpty) return;

        float scale = sizePx / 24f;
        canvas.Save();
        canvas.Translate(center.X + offsetX, center.Y + offsetY);
        canvas.Scale(scale, scale);
        canvas.Translate(-12f, -12f);
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawPath(path, paint);
        canvas.Restore();
    }
}
