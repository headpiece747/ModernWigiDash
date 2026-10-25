using ModernWigiDash.Sdk;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// The poster grid's cell geometry, computed once per frame from the
/// placement bounds, the responsive column/row counts, the game count, and
/// the horizontal scroll offset. <see cref="Indexes"/> maps each cell to its
/// game (or -1 for an empty tail cell), so the renderer, the poster requests,
/// and the touch hit test all read one mapping and can never drift apart
/// (the NowPlayingLayout house rule).
/// </summary>
public readonly record struct SteamGridGeometry(
    int Columns,
    int Rows,
    SKRect[] Cells,
    int[] Indexes,
    float CellWidth,
    float CellHeight,
    float ScrollX,
    float MaxScroll)
{
    /// <summary>The total cell count (columns × rows).</summary>
    public int CellCount => Cells.Length;
}

/// <summary>
/// Pure layout rules for the Steam poster grid: the responsive column count
/// for a width, the cell size from the 2:3 poster aspect, the row count that
/// fits AND keeps the visible cells inside the poster-cache budget, and the
/// cell rects + game indexes offset by the horizontal scroll. The design
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

    /// <summary>
    /// The visible-cell ceiling: the poster cache's 24-slot capacity. The
    /// layout never shows more cells than the cache can hold, so every
    /// visible poster can be resident at once (a larger grid would evict
    /// cells it is still drawing and flicker between posters and
    /// placeholders). <c>SteamGridLayoutTests</c> pins this to
    /// <c>SteamPosterCache.Capacity</c>.
    /// </summary>
    public const int MaxVisibleCells = 24;

    /// <summary>
    /// The minimum design-space width a column must get before a row wraps.
    /// 120 gives 8 columns at the design width and 8 × 3 = 24 cells at the
    /// default aspect, exactly the cache budget.
    /// </summary>
    private const float MinCellWidth = 120f;

    /// <summary>
    /// The responsive column count for a bounds width: as many columns as fit
    /// at the minimum cell width, clamped to at least one. The scale cancels,
    /// so the count is constant for a given design width (8 at 1016 px).
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
    /// the row count that fits the height and the cache budget, and every
    /// visible cell's rect plus the game index it displays. The list is laid
    /// out as horizontal shelves (one per row), so a horizontal scroll reveals
    /// later games instead of sliding the first page off-screen.
    /// </summary>
    /// <param name="bounds">The widget's local bounds.</param>
    /// <param name="columns">The column count (from <see cref="ColumnsForWidth"/>).</param>
    /// <param name="scrollX">The horizontal scroll offset in device px.</param>
    /// <param name="gameCount">The number of games the grid can draw.</param>
    public static SteamGridGeometry Compute(SKRect bounds, int columns, float scrollX, int gameCount)
    {
        columns = Math.Max(1, columns);
        float pad = 16f * (bounds.Width / DesignWidth);
        float gap = 12f * (bounds.Width / DesignWidth);

        float cellWidth = (bounds.Width - pad * 2f - gap * (columns - 1)) / columns;
        cellWidth = Math.Max(1f, cellWidth);
        float cellHeight = cellWidth * PosterAspect;

        int fitRows = Math.Max(1, (int)((bounds.Height - pad * 2f) / (cellHeight + gap)));
        int rows = Math.Min(fitRows, Math.Max(1, MaxVisibleCells / columns));

        // Each row is an independent shelf of the library; the shelf length
        // is what the horizontal scroll traverses. A short library (fewer
        // games than one page) keeps one page so the row-major order matches
        // the data order.
        int shelfLength = Math.Max(columns, (int)Math.Ceiling(gameCount / (double)rows));
        float step = cellWidth + gap;
        float maxScroll = Math.Max(0f, (shelfLength - columns) * step);
        float clampedScroll = Math.Clamp(scrollX, 0f, maxScroll);
        int startColumn = Math.Min((int)(clampedScroll / step), Math.Max(0, shelfLength - columns));
        float fractional = clampedScroll - startColumn * step;

        var cells = new SKRect[rows * columns];
        var indexes = new int[cells.Length];
        for (int row = 0; row < rows; row++)
        {
            float y = bounds.Top + pad + row * (cellHeight + gap);
            for (int col = 0; col < columns; col++)
            {
                float x = bounds.Left + pad + col * step - fractional;
                int i = row * columns + col;
                cells[i] = new SKRect(x, y, x + cellWidth, y + cellHeight);

                int gameIndex = row * shelfLength + startColumn + col;
                indexes[i] = gameIndex < gameCount ? gameIndex : -1;
            }
        }

        return new SteamGridGeometry(columns, rows, cells, indexes, cellWidth, cellHeight, clampedScroll, maxScroll);
    }

    /// <summary>
    /// The game index whose cell contains <paramref name="point"/>, or -1
    /// when the point is on no cell (gaps, margins, an empty tail cell, or
    /// past the last column).
    /// </summary>
    public static int HitTest(SteamGridGeometry geometry, SKPoint point)
    {
        for (int i = 0; i < geometry.Cells.Length; i++)
        {
            if (geometry.Indexes[i] >= 0 && geometry.Cells[i].Contains(point))
                return geometry.Indexes[i];
        }

        return -1;
    }
}
