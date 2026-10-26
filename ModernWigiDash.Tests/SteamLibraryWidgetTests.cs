using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

/// <summary>
/// The Steam Library widget's touch routing and persistence: a short tap
/// launches the hit title (the grid cell or the showcase focal card), a 600 ms
/// long-press toggles the favorite under the finger (persisted through the one
/// <c>SetProperty</c> path), and a drag scrolls. The library source, launch
/// sink, and clock are all fakes, so no real Steam install, process, or wall
/// clock is touched; the test drives the service's synchronous <c>RefreshNow</c>
/// seam instead of the 3-second loop.
/// </summary>
[TestClass]
public sealed class SteamLibraryWidgetTests
{
    /// <summary>The design-space bounds every test renders into (the touch geometry's base).</summary>
    private static readonly SKRect Bounds = new(0, 0, SteamGridLayout.DesignWidth, SteamGridLayout.DesignHeight);

    /// <summary>A fake library source: a fixed manifest list, no installation path (no disk warmup).</summary>
    private sealed class FakeSource : ISteamLibrarySource
    {
        private readonly IReadOnlyList<SteamAppManifest> _manifests;

        public FakeSource(params SteamAppManifest[] manifests) => _manifests = manifests;

        public string? SteamPath => null;

        public IReadOnlyList<SteamAppManifest> LoadManifests(string? steamPath) => _manifests;
    }

    /// <summary>An installed app manifest (idle, launchable); the ACF's executable drives the process key.</summary>
    private static SteamAppManifest Game(uint appId, string name = "Game", SteamAppState flags = SteamAppState.FullyInstalled)
        => new(appId, name, $"dir-{appId}", "game.exe", flags, 0, 0, 0, 10.0);

    /// <summary>
    /// The shared test rig: a widget over an installed, launchable library of
    /// <paramref name="gameCount"/> titles (ids 100 upward), a captured launch
    /// sink, and a controllable clock. The snapshot is produced through the
    /// synchronous <c>RefreshNow</c> seam and the bounds are fixed by one
    /// render, so the touch geometry is known.
    /// </summary>
    private static async ValueTask<(SteamLibraryWidget Widget, List<string> Launched, FakeTimeProvider Clock)> RigAsync(int gameCount = 2, string layoutMode = "Poster Grid")
    {
        var source = new FakeSource([.. Enumerable.Range(0, gameCount).Select(i => Game((uint)(100 + i), $"Game {i}"))]);
        List<string> launched = [];
        var clock = new FakeTimeProvider();
        var widget = new SteamLibraryWidget(source, launched.Add, clock) { LayoutMode = layoutMode };
        await widget.InitializeAsync(new TestContext()).ConfigureAwait(false);
        widget.ServiceForTest!.RefreshNow();
        using SKBitmap bitmap = new(1, 1);
        widget.Render(new SKCanvas(bitmap), Bounds);
        return (widget, launched, clock);
    }

    /// <summary>The shared rig over an explicit manifest list (the state-fold fixtures).</summary>
    private static async ValueTask<(SteamLibraryWidget Widget, List<string> Launched, FakeTimeProvider Clock)> RigManifestsAsync(params SteamAppManifest[] manifests)
    {
        var source = new FakeSource(manifests);
        List<string> launched = [];
        var clock = new FakeTimeProvider();
        var widget = new SteamLibraryWidget(source, launched.Add, clock) { LayoutMode = "Poster Grid" };
        await widget.InitializeAsync(new TestContext()).ConfigureAwait(false);
        widget.ServiceForTest!.RefreshNow();
        using SKBitmap bitmap = new(1, 1);
        widget.Render(new SKCanvas(bitmap), Bounds);
        return (widget, launched, clock);
    }

    /// <summary>The grid cell center for cell <paramref name="index"/> at the fixed bounds
    /// and <paramref name="scrollX"/>.</summary>
    private static SKPoint GridCellCenter(int index, int gameCount, float scrollX = 0f)
    {
        int columns = SteamGridLayout.ColumnsForWidth(Bounds.Width);
        SteamGridGeometry geometry = SteamGridLayout.Compute(Bounds, columns, scrollX, gameCount);
        SKRect cell = geometry.Cells[index];
        return new SKPoint((cell.Left + cell.Right) / 2f, (cell.Top + cell.Bottom) / 2f);
    }

    /// <summary>The showcase focal-card center at the fixed bounds (focus is the first title, so no previous).</summary>
    private static SKPoint ShowcaseFocusCenter()
    {
        SteamShowcaseGeometry geometry = SteamShowcaseLayout.Compute(Bounds, hasPrevious: false, hasNext: true);
        SKRect card = geometry.FocusCard;
        return new SKPoint((card.Left + card.Right) / 2f, (card.Top + card.Bottom) / 2f);
    }

    /// <summary>Delivers a touch pair (down at t0, up after <paramref name="press"/>) at <paramref name="point"/>.</summary>
    private static void Touch(SteamLibraryWidget widget, SKPoint point, FakeTimeProvider clock, TimeSpan press)
    {
        widget.OnTouch(point, TouchEventType.TouchDown);
        clock.Advance(press);
        widget.OnTouch(point, TouchEventType.TouchUp);
    }

    [TestMethod]
    public async ValueTask GridTap_LaunchesTheHitGame()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();

        Touch(widget, GridCellCenter(0, 2), clock, TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { "steam://run/100" }, launched);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask GridTap_SecondCell_LaunchesTheSecondGame()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();

        Touch(widget, GridCellCenter(1, 2), clock, TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { "steam://run/101" }, launched);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask ShowcaseTap_LaunchesTheFocusedGame()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();
        widget.LayoutMode = "Showcase";

        Touch(widget, ShowcaseFocusCenter(), clock, TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { "steam://run/100" }, launched);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask GridTapAndLongPress_TheSamePoint_ActOnTheSameGame()
    {
        // One hit entry serves both gestures, so a tap and a long-press at the
        // same point can never resolve to different games.
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();
        SKPoint point = GridCellCenter(1, 2);

        Touch(widget, point, clock, TimeSpan.Zero);
        CollectionAssert.AreEqual(new[] { "steam://run/101" }, launched);

        Touch(widget, point, clock, SteamLibraryWidget.LongPress + TimeSpan.FromMilliseconds(50));

        CollectionAssert.AreEqual(new[] { "steam://run/101" }, launched, "a long press must not launch");
        Assert.AreEqual("[101]", widget.ServiceForTest!.FavoritesJson, "a long press must favorite the game the tap would have launched");
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask ShowcaseTapAndLongPress_TheFocalCardActsOnTheFocusedGame()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();
        widget.LayoutMode = "Showcase";
        SKPoint point = ShowcaseFocusCenter();

        Touch(widget, point, clock, TimeSpan.Zero);
        CollectionAssert.AreEqual(new[] { "steam://run/100" }, launched);

        Touch(widget, point, clock, SteamLibraryWidget.LongPress + TimeSpan.FromMilliseconds(50));

        CollectionAssert.AreEqual(new[] { "steam://run/100" }, launched, "a long press must not launch");
        Assert.AreEqual("[100]", widget.ServiceForTest!.FavoritesJson);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask LongPress_TogglesTheFavoriteUnderTheFinger()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();

        Touch(widget, GridCellCenter(0, 2), clock, SteamLibraryWidget.LongPress + TimeSpan.FromMilliseconds(50));

        // A long press favorites, it never launches.
        Assert.IsEmpty(launched);
        Assert.AreEqual("[100]", widget.ServiceForTest!.FavoritesJson);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask ShortTap_DoesNotFavorite()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();

        Touch(widget, GridCellCenter(0, 2), clock, TimeSpan.FromMilliseconds(100));

        CollectionAssert.AreEqual(new[] { "steam://run/100" }, launched);
        Assert.AreEqual("[]", widget.ServiceForTest!.FavoritesJson);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask LongPress_TogglingOff_RemovesTheFavorite()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync();
        widget.ServiceForTest!.SetFavoritesJson("[100]");

        Touch(widget, GridCellCenter(0, 2), clock, SteamLibraryWidget.LongPress + TimeSpan.FromMilliseconds(50));

        Assert.IsEmpty(launched);
        Assert.AreEqual("[]", widget.ServiceForTest.FavoritesJson);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask GridDrag_ScrollsToLaterTitles()
    {
        const int gameCount = 30;
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync(gameCount);

        int columns = SteamGridLayout.ColumnsForWidth(Bounds.Width);
        SteamGridGeometry atRest = SteamGridLayout.Compute(Bounds, columns, 0f, gameCount);
        float step = atRest.Cells[1].Left - atRest.Cells[0].Left;
        SKPoint from = GridCellCenter(0, gameCount);
        SKPoint to = new(from.X - step, from.Y);

        // Drag left by one cell step: the same screen position must now show a
        // later title (the shelf scroll), not the first page shifted left.
        widget.OnTouch(from, TouchEventType.TouchDown);
        widget.OnTouch(to, TouchEventType.TouchUp);

        int revealed = SteamGridLayout
            .Compute(Bounds, columns, Math.Min(step, atRest.MaxScroll), gameCount)
            .Indexes[0];
        Assert.IsTrue(revealed > 0, "the drag must reveal a later title");

        Touch(widget, from, clock, TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { $"steam://run/{100 + revealed}" }, launched);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask GridTap_RunningTitle_NeverRelaunchesIt()
    {
        // The ACF's AppRunning flag with no resolvable executable: the tracker
        // folds the title to Running, and the tap must route to terminate (a
        // blank process name makes that a no-op) instead of a second launch.
        var manifest = new SteamAppManifest(100, "Alpha", "dir-100", "", SteamAppState.AppRunning, 0, 0, 0, 0);
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigManifestsAsync(manifest);

        Touch(widget, GridCellCenter(0, 1), clock, TimeSpan.Zero);

        Assert.IsEmpty(launched, "a tap on a running title must not launch it again");
        await widget.DisposeAsync();
    }

    private static SteamGameItem GameItem(uint appId, string name = "Game", SteamGameState state = SteamGameState.Idle)
        => new(appId, name, $"dir-{appId}", state, 0f, 10.0, null, false);

    [TestMethod]
    public async ValueTask HeroDeck_ActionTap_LaunchesTheHeroGame()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync(2, "Hero Deck");

        SteamHeroGeometry geom = SteamHeroLayout.Compute(Bounds, [GameItem(100), GameItem(101)], selectedAppId: 100);
        SKPoint actionBtnCenter = new(geom.HeroActionButtonBounds.MidX, geom.HeroActionButtonBounds.MidY);

        Touch(widget, actionBtnCenter, clock, TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { "steam://run/100" }, launched);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask HeroDeck_ModeToggleTap_CyclesLayouts()
    {
        (SteamLibraryWidget widget, _, FakeTimeProvider clock) = await RigAsync(2, "Hero Deck");

        SteamHeroGeometry geom = SteamHeroLayout.Compute(Bounds, [GameItem(100), GameItem(101)], selectedAppId: 100);
        SKPoint toggleCenter = new(geom.LayoutToggleBounds.MidX, geom.LayoutToggleBounds.MidY);

        Touch(widget, toggleCenter, clock, TimeSpan.Zero);
        Assert.AreEqual("Showcase", widget.LayoutMode);

        Touch(widget, toggleCenter, clock, TimeSpan.Zero);
        Assert.AreEqual("Poster Grid", widget.LayoutMode);

        Touch(widget, toggleCenter, clock, TimeSpan.Zero);
        Assert.AreEqual("Hero Deck", widget.LayoutMode);

        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask HeroDeck_ShelfTap_ChangesHeroAndLaunches()
    {
        (SteamLibraryWidget widget, List<string> launched, FakeTimeProvider clock) = await RigAsync(2, "Hero Deck");

        SteamHeroGeometry geom = SteamHeroLayout.Compute(Bounds, [GameItem(100), GameItem(101)], selectedAppId: 100);
        SKPoint shelfRow1Center = new(geom.ShelfItems[1].Bounds.MidX, geom.ShelfItems[1].Bounds.MidY);

        // Tap shelf item 1 (game 101)
        Touch(widget, shelfRow1Center, clock, TimeSpan.Zero);

        // Re-render so hero updates
        using SKBitmap bitmap = new(1, 1);
        widget.Render(new SKCanvas(bitmap), Bounds);

        // Now tap action button
        SKPoint actionBtnCenter = new(geom.HeroActionButtonBounds.MidX, geom.HeroActionButtonBounds.MidY);
        Touch(widget, actionBtnCenter, clock, TimeSpan.Zero);

        CollectionAssert.AreEqual(new[] { "steam://run/101" }, launched);
        await widget.DisposeAsync();
    }

    [TestMethod]
    public async ValueTask HeroDeck_LongPress_TogglesFavorite()
    {
        (SteamLibraryWidget widget, _, FakeTimeProvider clock) = await RigAsync(2, "Hero Deck");

        SteamHeroGeometry geom = SteamHeroLayout.Compute(Bounds, [GameItem(100), GameItem(101)], selectedAppId: 100);
        SKPoint shelfRow0Center = new(geom.ShelfItems[0].Bounds.MidX, geom.ShelfItems[0].Bounds.MidY);

        Touch(widget, shelfRow0Center, clock, SteamLibraryWidget.LongPress + TimeSpan.FromMilliseconds(50));

        Assert.IsTrue(widget.FavoritesJson.Contains("100"), "Long press on shelf item must favorite it");
        await widget.DisposeAsync();
    }
}
