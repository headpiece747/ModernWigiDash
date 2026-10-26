namespace ModernWigiDash.Tests;

using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;
using ModernWigiDash.Widgets.Steam.Services;

/// <summary>
/// The Steam render-view memo's pins: the recompute-on-change rule (a
/// steady-state frame reuses the cached record, a changed input recomputes),
/// the <c>Invalidate</c> reset, and the filtered-game list keyed on the
/// published snapshot reference + the Show-Uninstalled flag.
/// </summary>
[TestClass]
public class SteamGeometryMemoTests
{
    private static readonly SKSize Fallback = new(1016f, 592f);

    [TestMethod]
    public void Grid_SteadyState_ReuseTheCachedRecord()
    {
        var memo = new SteamGeometryMemo();
        SKRect bounds = new(0, 0, 1016f, 592f);

        var first = memo.Grid(bounds, 0f, 12, Fallback);
        var second = memo.Grid(bounds, 0f, 12, Fallback);

        Assert.AreEqual(first, second, "a steady-state frame reuses the cached record");
    }

    [TestMethod]
    public void Grid_ScrollChange_Recomputes()
    {
        var memo = new SteamGeometryMemo();
        SKRect bounds = new(0, 0, 1016f, 592f);

        var first = memo.Grid(bounds, 0f, 12, Fallback);
        var second = memo.Grid(bounds, 50f, 12, Fallback);

        Assert.AreNotEqual(first, second, "a scroll change recomputes the grid record");
    }

    [TestMethod]
    public void Grid_GameCountChange_Recomputes()
    {
        var memo = new SteamGeometryMemo();
        SKRect bounds = new(0, 0, 1016f, 592f);

        var first = memo.Grid(bounds, 0f, 12, Fallback);
        var second = memo.Grid(bounds, 0f, 24, Fallback);

        Assert.AreNotEqual(first, second, "a game-count change recomputes the grid record");
    }

    [TestMethod]
    public void Showcase_SteadyState_ReuseTheCachedRecord()
    {
        var memo = new SteamGeometryMemo();
        SKRect bounds = new(0, 0, 1016f, 592f);

        var first = memo.Showcase(bounds, true, true, Fallback);
        var second = memo.Showcase(bounds, true, true, Fallback);

        Assert.AreEqual(first, second, "a steady-state frame reuses the cached showcase record");
    }

    [TestMethod]
    public void Showcase_FlankChange_Recomputes()
    {
        var memo = new SteamGeometryMemo();
        SKRect bounds = new(0, 0, 1016f, 592f);

        var first = memo.Showcase(bounds, true, true, Fallback);
        var second = memo.Showcase(bounds, false, true, Fallback);

        Assert.AreNotEqual(first, second, "a flank-visibility change recomputes the showcase record");
    }

    [TestMethod]
    public void FilteredGames_SteadyState_ReuseTheCachedList()
    {
        var memo = new SteamGeometryMemo();
        var snapshot = MakeSnapshot(3);

        var first = memo.FilteredGames(snapshot, showUninstalled: true);
        var second = memo.FilteredGames(snapshot, showUninstalled: true);

        Assert.AreSame(first, second, "a steady-state tick reuses the cached filtered list");
    }

    [TestMethod]
    public void FilteredGames_SnapshotChange_Recomputes()
    {
        var memo = new SteamGeometryMemo();
        var snapshot1 = MakeSnapshot(3);
        var snapshot2 = MakeSnapshot(5);

        var first = memo.FilteredGames(snapshot1, showUninstalled: true);
        var second = memo.FilteredGames(snapshot2, showUninstalled: true);

        Assert.AreNotSame(first, second, "a new snapshot reference recomputes the filtered list");
    }

    [TestMethod]
    public void FilteredGames_FlagChange_Recomputes()
    {
        var memo = new SteamGeometryMemo();
        var snapshot = MakeSnapshotWithUninstalled(3, 1);

        var installed = memo.FilteredGames(snapshot, showUninstalled: false);
        var all = memo.FilteredGames(snapshot, showUninstalled: true);

        Assert.AreNotSame(installed, all, "a flag change recomputes the filtered list");
        Assert.AreEqual(4, all.Count, "show-uninstalled returns all games (3 installed + 1 uninstalled)");
        Assert.AreEqual(3, installed.Count, "installed-only filters out the uninstalled game");
    }

    [TestMethod]
    public void Invalidate_ClearsTheMemoState()
    {
        var memo = new SteamGeometryMemo();
        SKRect bounds = new(0, 0, 1016f, 592f);
        var snapshot = MakeSnapshotWithUninstalled(3, 1);

        var grid = memo.Grid(bounds, 0f, 4, Fallback);
        var filtered = memo.FilteredGames(snapshot, showUninstalled: false);

        memo.Invalidate();

        var gridAfter = memo.Grid(bounds, 0f, 4, Fallback);
        var filteredAfter = memo.FilteredGames(snapshot, showUninstalled: false);

        Assert.AreNotEqual(grid, gridAfter, "Invalidate forces a grid recompute");
        Assert.AreNotSame(filtered, filteredAfter, "Invalidate forces a filtered-list recompute (installed-only builds a fresh list)");
    }

    [TestMethod]
    public void Selection_EmptyView_SelectsNothing()
    {
        var memo = new SteamGeometryMemo();

        var selection = memo.Selection([]);

        Assert.IsNull(selection.Focus);
        Assert.IsNull(selection.Previous);
        Assert.IsNull(selection.Next);
    }

    [TestMethod]
    public void Selection_ClampsAStaleCursorToTheShrunkenView()
    {
        var memo = new SteamGeometryMemo();
        IReadOnlyList<SteamGameItem> three = [Game(1), Game(2), Game(3)];
        IReadOnlyList<SteamGameItem> one = [Game(1)];

        memo.Snap(three, 1);
        memo.Snap(three, 1);
        Assert.AreEqual(three[2], memo.Selection(three).Focus, "the cursor walks to the last card");

        var selection = memo.Selection(one);

        Assert.AreEqual(one[0], selection.Focus, "a view that shrank clamps the stale cursor to its range");
        Assert.IsNull(selection.Next, "the clamped cursor has no next card in the shrunken view");
    }

    [TestMethod]
    public void Snap_ClampsToTheViewRange_BothDirections()
    {
        var memo = new SteamGeometryMemo();
        IReadOnlyList<SteamGameItem> two = [Game(1), Game(2)];

        memo.Snap(two, -1);
        Assert.AreEqual(two[0], memo.Selection(two).Focus, "a snap below zero clamps to the first card");

        memo.Snap(two, 1);
        memo.Snap(two, 1);
        memo.Snap(two, 1);
        Assert.AreEqual(two[1], memo.Selection(two).Focus, "snaps past the end clamp to the last card");
    }

    private static SteamGameItem Game(uint appId, SteamGameState state = SteamGameState.Idle)
        => new(appId, "Game " + appId, "C:\\Games", state, 0f, 0.0, null, false);

    private static SteamLibrarySnapshot MakeSnapshot(int count)
    {
        var games = new List<SteamGameItem>(count);
        for (int i = 0; i < count; i++)
        {
            games.Add(new SteamGameItem((uint)(1000 + i), $"Game {i}", $"/games/{i}", SteamGameState.Idle, 0f, 0, null, false));
        }
        return new SteamLibrarySnapshot(games, null, true, null);
    }

    private static SteamLibrarySnapshot MakeSnapshotWithUninstalled(int installed, int uninstalled)
    {
        var games = new List<SteamGameItem>();
        for (int i = 0; i < installed; i++)
        {
            games.Add(new SteamGameItem((uint)(1000 + i), $"Game {i}", $"/games/{i}", SteamGameState.Idle, 0f, 0, null, false));
        }
        for (int i = 0; i < uninstalled; i++)
        {
            games.Add(new SteamGameItem((uint)(2000 + i), $"Uninstalled {i}", $"/games/u{i}", SteamGameState.Uninstalled, 0f, 0, null, false));
        }
        return new SteamLibrarySnapshot(games, null, true, null);
    }
}
