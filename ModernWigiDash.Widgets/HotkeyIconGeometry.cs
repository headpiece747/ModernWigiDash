namespace ModernWigiDash.Widgets;

/// <summary>
/// Mathematical and layout calculations for hotkey button icons: anchor positioning,
/// bounds clamping, hit testing, and drag-and-drop grab offsets.
/// </summary>
public static class HotkeyIconGeometry
{
    private const float MaxHeightRatio = 0.62f;
    private const float AutoSizeRatio = 0.4f;
    private const float AnchorYRatio = 0.31f;

    /// <summary>
    /// Computes the center and radius (half-size) of the icon within the specified widget bounds.
    /// </summary>
    public static bool Compute(
        float width,
        float height,
        int iconSize,
        int offsetX,
        int offsetY,
        bool hasIcon,
        out SKPoint center,
        out float half)
    {
        if (!hasIcon)
        {
            center = default;
            half = 0f;
            return false;
        }

        float maxIconSize = Math.Min(width, height * MaxHeightRatio);
        float size = iconSize > 0 ? iconSize : Math.Min(width, height) * AutoSizeRatio;
        size = Math.Clamp(size, 0f, maxIconSize);
        half = size / 2f;
        if (half <= 0f)
        {
            center = default;
            return false;
        }

        center = new SKPoint(
            Math.Clamp(width / 2f + offsetX, half, width - half),
            Math.Clamp(height * AnchorYRatio + offsetY, half, height - half));
        return true;
    }

    /// <summary>
    /// Evaluates whether a local (x, y) coordinate falls within the icon's circular hit area.
    /// </summary>
    public static bool IsPointOver(
        float width,
        float height,
        int iconSize,
        int offsetX,
        int offsetY,
        bool hasIcon,
        float localX,
        float localY)
    {
        if (!Compute(width, height, iconSize, offsetX, offsetY, hasIcon, out var center, out float half))
            return false;

        float dx = localX - center.X;
        float dy = localY - center.Y;
        return dx * dx + dy * dy <= half * half;
    }

    /// <summary>
    /// Calculates the updated persistent icon offsets resulting from a drag-move gesture,
    /// clamping the icon center safely within the widget's physical boundary.
    /// </summary>
    public static bool TryCalculateGrabMove(
        float width,
        float height,
        int iconSize,
        int currentOffsetX,
        int currentOffsetY,
        bool hasIcon,
        float localX,
        float localY,
        float grabOffsetX,
        float grabOffsetY,
        out int newX,
        out int newY)
    {
        if (!Compute(width, height, iconSize, currentOffsetX, currentOffsetY, hasIcon, out _, out float half))
        {
            newX = currentOffsetX;
            newY = currentOffsetY;
            return false;
        }

        float cx = Math.Clamp(localX + grabOffsetX, half, width - half);
        float cy = Math.Clamp(localY + grabOffsetY, half, height - half);
        newX = (int)Math.Round(cx - width / 2f);
        newY = (int)Math.Round(cy - height * AnchorYRatio);
        return newX != currentOffsetX || newY != currentOffsetY;
    }
}
