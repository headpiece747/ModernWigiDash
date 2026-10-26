using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;

namespace ModernWigiDash.Tests;

[TestClass]
public sealed class SteamHeroLayoutTests
{
    private static readonly SKRect StandardBounds = new(0, 0, 1016, 592);

    private static SteamGameItem MakeGame(uint id, string name, SteamGameState state = SteamGameState.Idle, double hours = 50.0)
        => new(id, name, $"dir-{id}", state, 0f, hours, DateTime.UtcNow.AddDays(-1), false);

    [TestMethod]
    public void Compute_Standard1016x592_ProducesNonOverlappingRegions()
    {
        var games = new[]
        {
            MakeGame(10, "Cyberpunk 2077", SteamGameState.Running, 120.0),
            MakeGame(20, "Elden Ring", SteamGameState.Idle, 85.0),
            MakeGame(30, "Hades II", SteamGameState.Idle, 30.0),
        };

        SteamHeroGeometry geom = SteamHeroLayout.Compute(StandardBounds, games, selectedAppId: 10, scrollY: 0f);

        // Header sits above content
        Assert.IsTrue(geom.HeaderBounds.Top >= StandardBounds.Top);
        Assert.IsTrue(geom.HeaderBounds.Bottom < geom.HeroCardBounds.Top);
        Assert.IsTrue(geom.HeaderBounds.Bottom < geom.ShelfBounds.Top);

        // Hero card is on the left, shelf is on the right
        Assert.IsTrue(geom.HeroCardBounds.Left < geom.ShelfBounds.Left);
        Assert.IsTrue(geom.HeroCardBounds.Right <= geom.ShelfBounds.Left);
        Assert.IsTrue(geom.ShelfBounds.Right <= StandardBounds.Right);

        // Action button is inside HeroCard and has ergonomic finger touch height
        Assert.IsTrue(geom.HeroCardBounds.Contains(geom.HeroActionButtonBounds));
        Assert.IsTrue(geom.HeroActionButtonBounds.Height >= 50f, $"Action button height {geom.HeroActionButtonBounds.Height} should be >= 50px");

        // Filter pills placed left-to-right
        Assert.IsTrue(geom.FilterAllBounds.Right <= geom.FilterFavoritesBounds.Left);
        Assert.IsTrue(geom.FilterFavoritesBounds.Right <= geom.FilterRecentBounds.Left);

        // Layout toggle is on far right
        Assert.IsTrue(geom.LayoutToggleBounds.Right <= StandardBounds.Right);
        Assert.IsTrue(geom.FilterRecentBounds.Right < geom.LayoutToggleBounds.Left);

        // Shelf has 3 items
        Assert.AreEqual(3, geom.ShelfItems.Length);
        Assert.IsTrue(geom.ShelfItems[0].IsSelected);
        Assert.IsFalse(geom.ShelfItems[1].IsSelected);
    }

    [TestMethod]
    public void HitTest_DispatchesTargetAccurately()
    {
        var games = new[]
        {
            MakeGame(10, "Cyberpunk 2077"),
            MakeGame(20, "Elden Ring"),
        };

        SteamHeroGeometry geom = SteamHeroLayout.Compute(StandardBounds, games, selectedAppId: 10);

        // 1. Layout toggle
        SKPoint toggleCenter = new((geom.LayoutToggleBounds.Left + geom.LayoutToggleBounds.Right) / 2f,
                                   (geom.LayoutToggleBounds.Top + geom.LayoutToggleBounds.Bottom) / 2f);
        var (hit1, _) = SteamHeroLayout.HitTest(geom, toggleCenter);
        Assert.AreEqual(SteamHeroHit.LayoutToggle, hit1);

        // 2. Filter all
        SKPoint allCenter = new((geom.FilterAllBounds.Left + geom.FilterAllBounds.Right) / 2f,
                                (geom.FilterAllBounds.Top + geom.FilterAllBounds.Bottom) / 2f);
        var (hit2, _) = SteamHeroLayout.HitTest(geom, allCenter);
        Assert.AreEqual(SteamHeroHit.FilterAll, hit2);

        // 3. Action button
        SKPoint btnCenter = new((geom.HeroActionButtonBounds.Left + geom.HeroActionButtonBounds.Right) / 2f,
                                (geom.HeroActionButtonBounds.Top + geom.HeroActionButtonBounds.Bottom) / 2f);
        var (hit3, _) = SteamHeroLayout.HitTest(geom, btnCenter);
        Assert.AreEqual(SteamHeroHit.HeroAction, hit3);

        // 4. Shelf item 1
        SKRect shelfRow1 = geom.ShelfItems[1].Bounds;
        SKPoint rowCenter = new((shelfRow1.Left + shelfRow1.Right) / 2f, (shelfRow1.Top + shelfRow1.Bottom) / 2f);
        var (hit4, index) = SteamHeroLayout.HitTest(geom, rowCenter);
        Assert.AreEqual(SteamHeroHit.ShelfItem, hit4);
        Assert.AreEqual(1, index);
    }

    [TestMethod]
    public void Compute_ScrollClamping_LimitsMaxScroll()
    {
        var games = new[] { MakeGame(10, "G1"), MakeGame(20, "G2") };
        SteamHeroGeometry geom = SteamHeroLayout.Compute(StandardBounds, games, selectedAppId: 10, scrollY: 9999f);

        // Clamped scroll should equal MaxScrollY
        Assert.AreEqual(geom.MaxScrollY, geom.ScrollY);
    }
}
