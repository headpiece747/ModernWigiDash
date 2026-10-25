using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// The poster grid's cell geometry, computed once per frame from the
/// placement bounds, the responsive column/row counts, and the horizontal
/// scroll offset. The render path draws the posters from these rects and the
/// touch path hit-tests the same rects, so the drawn cells and the tap
/// targets can never drift apart (the NowPlayingLayout house rule).
/// </summary>
public readonly record struct SteamGridGeometry(
    int Columns,
    int Rows,
    SKRect[] Cells,
    float CellWidth,
    float CellHeight,
    float ScrollX)
{
    /// <summary>The total cell count (columns × rows).</summary>
    public int CellCount => Cells.Length;
}

/// <summary>
/// Pure layout rules for the Steam poster grid: the responsive column count
/// for a width, the cell size from the 2:3 poster aspect, the row count that
/// fits, and the cell rects offset by the horizontal scroll. The design
/// constants live only here, never re-derived at a draw site.
/// </summary>
public static class SteamGridLayout
{
    /// <summary>The design-space width — the scale base for both render and touch.</summary>
    public const float DesignWidth = DisplayGeometry.FramebufferWidth;

    /// <summary>The design-space height — the scale base for both render and touch.</summary>
    public const float DesignHeight = DisplayGeometry.FramebufferHeight;

    /// <summary>The poster aspect: height is 1.5× the width (the 600×900 shape).</summary>
    public const float PosterAspect = 1.5f;

    /// <summary>The minimum design-space width a column must get before a row wraps.</summary>
    private const float MinCellWidth = 96f;

    /// <summary>
    /// The responsive column count for a bounds width: as many columns as fit
    /// at the minimum cell width, clamped to at least one.
    /// </summary>
    public static int ColumnsForWidth(float width)
    {
        if (width <= 0f)
            return 1;

        float scale = width / DesignWidth;
        int columns = (int)(width / (MinCellWidth * scale));
        return Math.Max(1, columns);
    }

    /// <summary>
    /// The grid geometry for a placement: the cell size (from the 2:3 aspect),
    /// the row count that fits the height, and every cell rect offset by
    /// <paramref name="scrollX"/> (a positive scroll shifts the cells left).
    /// </summary>
    public static SteamGridGeometry Compute(SKRect bounds, int columns, float scrollX)
    {
        columns = Math.Max(1, columns);
        float pad = 16f * (bounds.Width / DesignWidth);
        float gap = 12f * (bounds.Width / DesignWidth);

        float cellWidth = (bounds.Width - pad * 2f - gap * (columns - 1)) / columns;
        cellWidth = Math.Max(1f, cellWidth);
        float cellHeight = cellWidth * PosterAspect;

        int rows = Math.Max(1, (int)((bounds.Height - pad * 2f) / (cellHeight + gap)));
        var cells = new SKRect[rows * columns];
        for (int row = 0; row < rows; row++)
        {
            float y = bounds.Top + pad + row * (cellHeight + gap) - 0f; // no vertical scroll
            for (int col = 0; col < columns; col++)
            {
                float x = bounds.Left + pad + col * (cellWidth + gap) - scrollX;
                cells[row * columns + col] = new SKRect(x, y, x + cellWidth, y + cellHeight);
            }
        }

        return new SteamGridGeometry(columns, rows, cells, cellWidth, cellHeight, scrollX);
    }

    /// <summary>
    /// The cell index whose rect contains <paramref name="point"/>, or -1 when
    /// the point is on no cell (gaps, margins, or past the last column).
    /// </summary>
    public static int HitTest(SteamGridGeometry geometry, SKPoint point)
    {
        for (int i = 0; i < geometry.Cells.Length; i++)
        {
            if (geometry.Cells[i].Contains(point))
                return i;
        }

        return -1;
    }
}
