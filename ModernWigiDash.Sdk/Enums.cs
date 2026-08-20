using SkiaSharp;

namespace ModernWigiDash.Sdk;

/// <summary>
/// The normalized touch phases delivered to <see cref="IModernWidget.OnTouch"/>.
/// The App's touch pipeline normalizes the hardware protocol bytes exactly once
/// (TouchReport.ToEventType), so widgets never see vendor-specific data. Tap
/// actions conventionally fire on <see cref="TouchUp"/>.
/// </summary>
public enum TouchEventType
{
    /// <summary>Finger pressed the display.</summary>
    TouchDown,
    /// <summary>Finger lifted — the tap completion event.</summary>
    TouchUp,
    /// <summary>Finger moved while pressed (drag tracking).</summary>
    TouchMove
}

/// <summary>
/// The nominal placement size table for the display's 5×4 grid: columns × rows,
/// where one cell is 203×148 px (the table's nominal integers). Used for
/// widget default sizes and the size picker; snap-to-grid uses the exact
/// fractional cell (<see cref="GridSizeExtensions.CellWidth"/> = 1016/5 =
/// 203.2) so tiles never drift across the 1016 px width.
/// </summary>
public enum GridSizePreset
{
    /// <summary>1×1 cell: 203 × 148 px.</summary>
    Size1x1,
    /// <summary>2×1 cells: 406 × 148 px.</summary>
    Size2x1,
    /// <summary>3×1 cells: 609 × 148 px.</summary>
    Size3x1,
    /// <summary>4×1 cells: 813 × 148 px.</summary>
    Size4x1,
    /// <summary>5×1 cells: 1016 × 148 px.</summary>
    Size5x1,
    /// <summary>1×2 cells: 203 × 296 px.</summary>
    Size1x2,
    /// <summary>2×2 cells: 406 × 296 px.</summary>
    Size2x2,
    /// <summary>3×2 cells: 609 × 296 px.</summary>
    Size3x2,
    /// <summary>4×2 cells: 813 × 296 px.</summary>
    Size4x2,
    /// <summary>5×2 cells: 1016 × 296 px.</summary>
    Size5x2,
    /// <summary>1×3 cells: 203 × 444 px.</summary>
    Size1x3,
    /// <summary>2×3 cells: 406 × 444 px.</summary>
    Size2x3,
    /// <summary>3×3 cells: 609 × 444 px.</summary>
    Size3x3,
    /// <summary>4×3 cells: 813 × 444 px.</summary>
    Size4x3,
    /// <summary>5×3 cells: 1016 × 444 px.</summary>
    Size5x3,
    /// <summary>1×4 cells: 203 × 592 px.</summary>
    Size1x4,
    /// <summary>2×4 cells: 406 × 592 px.</summary>
    Size2x4,
    /// <summary>3×4 cells: 609 × 592 px.</summary>
    Size3x4,
    /// <summary>4×4 cells: 813 × 592 px.</summary>
    Size4x4,
    /// <summary>5×4 cells: 1016 × 592 px — the full framebuffer.</summary>
    Size5x4
}

/// <summary>
/// The grid-geometry rules derived from the single framebuffer source
/// (<see cref="DisplayGeometry"/>): the exact cell size the 5×4 grid tiles
/// into, and the one snap-to-grid rule placement centering and drag/resize
/// snapping share, so the rounding can never drift between Core and App.
/// </summary>
public static class GridSizeExtensions
{
    // Cell size derived from the single framebuffer geometry source:
    // 5 columns x 4 rows. Note the GridSizePreset table uses nominal integer
    // cells (203) for widget default sizes; the exact 203.2 is what
    // snap-to-grid needs to tile the 1016 px width without drift.
    /// <summary>The exact cell width: framebuffer width (1016 px) ÷ 5 columns
    /// = 203.2 px — the value snap-to-grid must use (the preset table's 203 is
    /// nominal).</summary>
    public const float CellWidth = DisplayGeometry.FramebufferWidth / 5f;

    /// <summary>The exact cell height: framebuffer height (592 px) ÷ 4 rows
    /// = 148 px (integer, identical to the preset table).</summary>
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

    /// <summary>The preset's nominal pixel size from the table (203×148 per
    /// cell); throws <see cref="ArgumentOutOfRangeException"/> for values
    /// outside the defined presets.</summary>
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
