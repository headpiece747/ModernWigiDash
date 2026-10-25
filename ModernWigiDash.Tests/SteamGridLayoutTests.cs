using ModernWigiDash.Widgets.Steam.Rendering;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

/// <summary>
/// The poster grid's layout rules: the responsive column count, the
/// visible-cell budget (never more cells than the poster cache can hold), the
/// shelf scroll that reveals later titles instead of shifting the first page
/// off-screen, and the hit test that reads the same index map the renderer
/// draws from.
/// </summary>
[TestClass]
public sealed class SteamGridLayoutTests
{
    /// <summary>The design-space bounds (the framebuffer the geometry is computed for).</summary>
    private static readonly SKRect Bounds = new(0, 0, SteamGridLayout.DesignWidth, SteamGridLayout.DesignHeight);

    [TestMethod]
    public void Compute_DesignBounds_StaysWithinThePosterCacheBudget()
    {
        SteamGridGeometry geometry = SteamGridLayout.Compute(Bounds, SteamGridLayout.ColumnsForWidth(Bounds.Width), 0f, 400);

        Assert.IsLessThanOrEqualTo(
            SteamPosterCache.Capacity,
            geometry.CellCount,
            "the grid must never draw more cells than the poster cache can hold (a larger grid evicts posters it is still drawing)");

        int capacity = SteamPosterCache.Capacity;
        int ceiling = SteamGridLayout.MaxVisibleCells;
        Assert.AreEqual(capacity, ceiling, "the layout ceiling and the cache capacity are one fact");
    }

    [TestMethod]
    public void Compute_Scrolled_RevealsLaterTitlesInTheSameCells()
    {
        const int gameCount = 30;
        int columns = SteamGridLayout.ColumnsForWidth(Bounds.Width);
        SteamGridGeometry atRest = SteamGridLayout.Compute(Bounds, columns, 0f, gameCount);
        float step = atRest.Cells[1].Left - atRest.Cells[0].Left;

        SteamGridGeometry scrolled = SteamGridLayout.Compute(Bounds, columns, step, gameCount);

        Assert.AreEqual(0, atRest.Indexes[0]);
        Assert.AreEqual(1, scrolled.Indexes[0], "a one-step scroll must show the next title in the first cell");
        Assert.AreEqual(step, atRest.Cells[0].Left + step - scrolled.Cells[0].Left, 1e-3, "the cells themselves stay on the same screen grid");
    }

    [TestMethod]
    public void Compute_ScrollBeyondTheEnd_ClampsToTheLastShelf()
    {
        const int gameCount = 30;
        int columns = SteamGridLayout.ColumnsForWidth(Bounds.Width);
        SteamGridGeometry geometry = SteamGridLayout.Compute(Bounds, columns, 100_000f, gameCount);

        Assert.AreEqual(geometry.MaxScroll, geometry.ScrollX, 1e-3, "an overshoot clamps to the last shelf");
        Assert.IsTrue(geometry.MaxScroll > 0f, "a 30-title library at the design size must be scrollable");
        Assert.AreEqual(gameCount - 1, geometry.Indexes.Max(), "the last title is reachable at full scroll");
    }

    [TestMethod]
    public void Compute_ShortLibrary_KeepsOnePageAndEmptyTailCells()
    {
        SteamGridGeometry geometry = SteamGridLayout.Compute(Bounds, SteamGridLayout.ColumnsForWidth(Bounds.Width), 0f, 2);

        Assert.AreEqual(0f, geometry.MaxScroll, "a library shorter than one page does not scroll");
        Assert.AreEqual(0, geometry.Indexes[0]);
        Assert.AreEqual(1, geometry.Indexes[1]);
        Assert.AreEqual(-1, geometry.Indexes[2], "a cell past the last title is an empty tail cell");
    }

    [TestMethod]
    public void HitTest_MapsTheCellToItsGameAndMissesTheEmptyTail()
    {
        SteamGridGeometry geometry = SteamGridLayout.Compute(Bounds, SteamGridLayout.ColumnsForWidth(Bounds.Width), 0f, 2);
        SKRect first = geometry.Cells[0];
        SKRect tail = geometry.Cells[2];

        Assert.AreEqual(0, SteamGridLayout.HitTest(geometry, new SKPoint(first.MidX, first.MidY)));
        Assert.AreEqual(-1, SteamGridLayout.HitTest(geometry, new SKPoint(tail.MidX, tail.MidY)), "an empty tail cell has no game to hit");
        Assert.AreEqual(-1, SteamGridLayout.HitTest(geometry, new SKPoint(-5f, -5f)), "a point outside the grid hits nothing");
    }
}
