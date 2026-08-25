namespace ModernWigiDash.Widgets;

/// <summary>
/// Shared parsed-SVG-path helpers used by the bundled Griddy icon set and the
/// runtime-loaded icon files: the draw-scaling protocol and the parse cache.
/// Split out of TextRenderHelper so the text/sparkline helpers stay free of
/// icon machinery.
/// </summary>
internal static class SvgIconHelper
{
    /// <summary>
    /// Draws <paramref name="path"/> centered at <paramref name="center"/> scaled to
    /// <paramref name="sizePx"/> by its largest bounds dimension, offset by
    /// <paramref name="offsetX"/>/<paramref name="offsetY"/>. Single shared scaling
    /// protocol for SVG-path icons.
    /// </summary>
    internal static void DrawPathScaled(SKCanvas canvas, SKPath path, SKPoint center, float sizePx, SKColor color, float offsetX, float offsetY)
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

    /// <summary>
    /// Shared parsed-SVG-path cache with an empty-path fallback. Keyed
    /// case-insensitively; callers supply the raw path data per key. The cache
    /// rule is the shared Sdk <see cref="SvgPathParseCache{T}"/>; the
    /// Skia-specific part is only the parser (bounds check, fill type) and the
    /// empty-path fallback.
    /// </summary>
    internal static class SvgPathCache
    {
        /// <summary>
        /// Returns the cached parsed path for <paramref name="key"/>, parsing
        /// <paramref name="pathData"/> on first use. Invalid or empty paths
        /// resolve to an empty <see cref="SKPath"/> (never null).
        /// </summary>
        internal static SKPath GetOrParse(string key, string pathData)
            => GetOrParse(key, () => pathData);

        /// <summary>
        /// Returns the cached parsed path for <paramref name="key"/>, parsing
        /// the path data produced by <paramref name="getPathData"/> on first
        /// use. Invalid or empty paths resolve to an empty <see cref="SKPath"/>
        /// (never null).
        /// </summary>
        internal static SKPath GetOrParse(string key, Func<string> getPathData)
            => SvgPathParseCache<SKPath>.GetOrParse(key, () => TryParse(getPathData())) ?? new SKPath();

        private static SKPath? TryParse(string pathData)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(pathData))
                    return null;

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
            return null;
        }
    }
}
