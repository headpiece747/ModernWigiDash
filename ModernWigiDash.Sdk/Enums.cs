using SkiaSharp;

namespace ModernWigiDash.Sdk;

public enum TouchEventType
{
    TouchDown,
    TouchUp,
    TouchMove
}

public enum GridSizePreset
{
    Size1x1, // 203 x 148 px
    Size2x1, // 406 x 148 px
    Size3x1, // 609 x 148 px
    Size4x1, // 813 x 148 px
    Size5x1, // 1016 x 148 px
    Size1x2, // 203 x 296 px
    Size2x2, // 406 x 296 px
    Size3x2, // 609 x 296 px
    Size4x2, // 813 x 296 px
    Size5x2, // 1016 x 296 px
    Size1x3, // 203 x 444 px
    Size2x3, // 406 x 444 px
    Size3x3, // 609 x 444 px
    Size4x3, // 813 x 444 px
    Size5x3, // 1016 x 444 px
    Size1x4, // 203 x 592 px
    Size2x4, // 406 x 592 px
    Size3x4, // 609 x 592 px
    Size4x4, // 813 x 592 px
    Size5x4 // 1016 x 592 px (Full Screen)
}

public static class GridSizeExtensions
{
    // Cell size derived from the single framebuffer geometry source:
    // 5 columns x 4 rows. Note the GridSizePreset table uses nominal integer
    // cells (203) for widget default sizes; the exact 203.2 is what
    // snap-to-grid needs to tile the 1016 px width without drift.
    public const float CellWidth = DisplayGeometry.FramebufferWidth / 5f;
    public const float CellHeight = DisplayGeometry.FramebufferHeight / 4f;

    /// <summary>Rounds a coordinate to the nearest whole cell — the single
    /// snap-to-grid rule (placement centering and the drag/resize snap share
    /// it, so the rounding can never drift between Core and App).</summary>
    public static float SnapToCell(float value, float cellSize)
        => (float)Math.Round(value / cellSize) * cellSize;

    /// <summary>Rounds an X coordinate to the horizontal grid.</summary>
    public static float SnapX(float value) => SnapToCell(value, CellWidth);

    /// <summary>Rounds a Y coordinate to the vertical grid.</summary>
    public static float SnapY(float value) => SnapToCell(value, CellHeight);

    public static SKSize ToSize(this GridSizePreset preset)
    {
        return preset switch
        {
            GridSizePreset.Size1x1 => new SKSize(203, 148),
            GridSizePreset.Size2x1 => new SKSize(406, 148),
            GridSizePreset.Size3x1 => new SKSize(609, 148),
            GridSizePreset.Size4x1 => new SKSize(813, 148),
            GridSizePreset.Size5x1 => new SKSize(1016, 148),
            GridSizePreset.Size1x2 => new SKSize(203, 296),
            GridSizePreset.Size2x2 => new SKSize(406, 296),
            GridSizePreset.Size3x2 => new SKSize(609, 296),
            GridSizePreset.Size4x2 => new SKSize(813, 296),
            GridSizePreset.Size5x2 => new SKSize(1016, 296),
            GridSizePreset.Size1x3 => new SKSize(203, 444),
            GridSizePreset.Size2x3 => new SKSize(406, 444),
            GridSizePreset.Size3x3 => new SKSize(609, 444),
            GridSizePreset.Size4x3 => new SKSize(813, 444),
            GridSizePreset.Size5x3 => new SKSize(1016, 444),
            GridSizePreset.Size1x4 => new SKSize(203, 592),
            GridSizePreset.Size2x4 => new SKSize(406, 592),
            GridSizePreset.Size3x4 => new SKSize(609, 592),
            GridSizePreset.Size4x4 => new SKSize(813, 592),
            GridSizePreset.Size5x4 => new SKSize(1016, 592),
            _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Unknown GridSizePreset")
        };
    }
}
