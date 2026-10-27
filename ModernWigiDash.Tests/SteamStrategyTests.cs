using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Rendering;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Tests;

[TestClass]
public sealed class SteamStrategyTests
{
    private static readonly SKSize Fallback = new(1016f, 592f);
    private static readonly SKRect Bounds = new(0, 0, 1016f, 592f);

    private static SteamGameItem Game(uint appId, string name = "Game")
        => new(appId, name, $"dir-{appId}", SteamGameState.Idle, 0f, 10.0, null, false);

    [TestMethod]
    public void HeroDeckStrategy_EmptyGames_ReturnsNoneOnTap()
    {
        var memo = new SteamGeometryMemo();
        using var strategy = new HeroDeckStrategy(memo);

        var result = strategy.HandleTap(new SKPoint(100, 100), Bounds, [], Fallback);

        Assert.AreEqual(SteamInteractionType.None, result.Type);
    }

    [TestMethod]
    public void HeroDeckStrategy_EmptyGames_ReturnsNullTargetAppId()
    {
        var memo = new SteamGeometryMemo();
        using var strategy = new HeroDeckStrategy(memo);

        uint? target = strategy.FindTargetAppId(new SKPoint(100, 100), Bounds, [], Fallback);

        Assert.IsNull(target);
    }

    [TestMethod]
    public void HeroDeckStrategy_PopulatesVisibleAppIds()
    {
        var memo = new SteamGeometryMemo();
        using var strategy = new HeroDeckStrategy(memo);

        IReadOnlyList<SteamGameItem> games = [Game(100), Game(101), Game(102)];
        List<uint> visible = [];
        strategy.GetVisibleAppIds(Bounds, games, Fallback, visible);

        Assert.IsTrue(visible.Contains(100u), "hero game must be in visible list");
        Assert.IsTrue(visible.Count >= 2, "shelf items in view must be included");
    }

    [TestMethod]
    public void ShowcaseStrategy_EmptyGames_ReturnsNoneOnTap()
    {
        var memo = new SteamGeometryMemo();
        using var strategy = new ShowcaseStrategy(memo);

        var result = strategy.HandleTap(new SKPoint(100, 100), Bounds, [], Fallback);

        Assert.AreEqual(SteamInteractionType.None, result.Type);
    }

    [TestMethod]
    public void ShowcaseStrategy_PopulatesVisibleAppIds()
    {
        var memo = new SteamGeometryMemo();
        using var strategy = new ShowcaseStrategy(memo);

        IReadOnlyList<SteamGameItem> games = [Game(100), Game(101), Game(102)];
        List<uint> visible = [];
        strategy.GetVisibleAppIds(Bounds, games, Fallback, visible);

        Assert.IsTrue(visible.Contains(100u), "focus game must be in visible list");
        Assert.IsTrue(visible.Contains(101u), "next game must be in visible list");
    }

    [TestMethod]
    public void GridStrategy_EmptyGames_ReturnsNoneOnTap()
    {
        var memo = new SteamGeometryMemo();
        using var strategy = new GridStrategy(memo);

        var result = strategy.HandleTap(new SKPoint(100, 100), Bounds, [], Fallback);

        Assert.AreEqual(SteamInteractionType.None, result.Type);
    }

    [TestMethod]
    public void GridStrategy_PopulatesVisibleAppIds()
    {
        var memo = new SteamGeometryMemo();
        using var strategy = new GridStrategy(memo);

        IReadOnlyList<SteamGameItem> games = [Game(100), Game(101), Game(102)];
        List<uint> visible = [];
        strategy.GetVisibleAppIds(Bounds, games, Fallback, visible);

        CollectionAssert.Contains(visible, 100u);
        CollectionAssert.Contains(visible, 101u);
        CollectionAssert.Contains(visible, 102u);
    }

    [TestMethod]
    public void ShowcaseLayout_HitTest_DisabledChevrons_ReturnsNone()
    {
        SteamShowcaseGeometry geom = SteamShowcaseLayout.Compute(Bounds, hasPrevious: false, hasNext: false);
        SKPoint leftChevronCenter = new(geom.LeftChevron.MidX, geom.LeftChevron.MidY);
        SKPoint rightChevronCenter = new(geom.RightChevron.MidX, geom.RightChevron.MidY);

        Assert.AreEqual(SteamShowcaseHit.None, SteamShowcaseLayout.HitTest(geom, leftChevronCenter));
        Assert.AreEqual(SteamShowcaseHit.None, SteamShowcaseLayout.HitTest(geom, rightChevronCenter));
    }

    [TestMethod]
    public void HeroDeckStrategy_Invalidate_ResetsScrollAndSelection()
    {
        var memo = new SteamGeometryMemo();
        using var strategy = new HeroDeckStrategy(memo);
        IReadOnlyList<SteamGameItem> games = [Game(100), Game(101)];

        strategy.HandleDrag(0f, 100f, Bounds, games, Fallback);
        strategy.Invalidate();

        uint? target = strategy.FindTargetAppId(new SKPoint(100, 100), Bounds, games, Fallback);
        Assert.IsNotNull(target);
    }

    [TestMethod]
    public void GridStrategy_Invalidate_ResetsScroll()
    {
        var memo = new SteamGeometryMemo();
        using var strategy = new GridStrategy(memo);
        IReadOnlyList<SteamGameItem> games = [Game(100), Game(101), Game(102)];

        strategy.HandleDrag(300f, 0f, Bounds, games, Fallback);
        strategy.Invalidate();

        var result = strategy.HandleTap(new SKPoint(50, 150), Bounds, games, Fallback);
        Assert.AreEqual(SteamInteractionType.ActivateGame, result.Type);
        Assert.AreEqual(100u, result.AppId, "scroll should reset to 0 after Invalidate so cell 0 is at origin");
    }
}
