using System.Runtime.InteropServices;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// Layout strategy for the Hero Deck mode: a prominent featured card on the left
/// with a vertical scrolling shelf of remaining titles on the right.
/// </summary>
internal sealed class HeroDeckStrategy : ISteamLayoutStrategy
{
    private readonly SteamHeroRenderer _renderer = new();
    private readonly SteamGeometryMemo _memo;
    private readonly List<uint> _visibleIdsBuffer = [];
    private uint _selectedHeroAppId;
    private float _shelfScrollY;
    private bool _disposed;

    public HeroDeckStrategy(SteamGeometryMemo memo)
    {
        _memo = memo;
    }

    public SteamLayoutMode Mode => SteamLayoutMode.HeroDeck;

    public void Render(
        SKCanvas canvas,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SteamRenderFrame frame,
        SteamLibrarySnapshot snapshot,
        SteamFilterMode filterMode,
        SKColor accent,
        SKColor text,
        SKSize fallback,
        SteamLibraryService service)
    {
        SteamGameItem? hero = ResolveHero(games);
        SteamHeroGeometry geom = _memo.Hero(bounds, games, _selectedHeroAppId, _shelfScrollY, fallback);

        _visibleIdsBuffer.Clear();
        PopulateVisibleIds(geom, hero, games, _visibleIdsBuffer);
        service.RequestPosters(games, CollectionsMarshal.AsSpan(_visibleIdsBuffer));

        _renderer.Render(canvas, bounds, geom, hero, games, frame.Posters, filterMode, accent, text);
    }

    public void GetVisibleAppIds(
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback,
        List<uint> destination)
    {
        SteamGameItem? hero = ResolveHero(games);
        SteamHeroGeometry geom = _memo.Hero(bounds, games, _selectedHeroAppId, _shelfScrollY, fallback);
        PopulateVisibleIds(geom, hero, games, destination);
    }

    public SteamInteractionResult HandleTap(
        SKPoint point,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback)
    {
        if (games.Count == 0)
            return SteamInteractionResult.None;

        SteamHeroGeometry geom = _memo.Hero(bounds, games, _selectedHeroAppId, _shelfScrollY, fallback);
        var (heroHit, shelfIndex) = SteamHeroLayout.HitTest(geom, point);

        switch (heroHit)
        {
            case SteamHeroHit.HeroAction:
                SteamGameItem? hero = ResolveHero(games);
                return hero is not null ? SteamInteractionResult.Activate(hero) : SteamInteractionResult.None;

            case SteamHeroHit.ShelfItem:
                if (shelfIndex >= 0 && shelfIndex < games.Count)
                {
                    _selectedHeroAppId = games[shelfIndex].AppId;
                    return SteamInteractionResult.Select(_selectedHeroAppId);
                }
                return SteamInteractionResult.None;

            case SteamHeroHit.HeroCard:
            default:
                return SteamInteractionResult.None;
        }
    }

    public void HandleDrag(
        float deltaX,
        float deltaY,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback)
    {
        SteamHeroGeometry geom = _memo.Hero(bounds, games, _selectedHeroAppId, _shelfScrollY, fallback);
        _shelfScrollY = Math.Clamp(_shelfScrollY - deltaY, 0f, geom.MaxScrollY);
    }

    public uint? FindTargetAppId(
        SKPoint point,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback)
    {
        if (games.Count == 0)
            return null;

        SteamHeroGeometry geom = _memo.Hero(bounds, games, _selectedHeroAppId, _shelfScrollY, fallback);
        var (heroHit, shelfIndex) = SteamHeroLayout.HitTest(geom, point);

        if (heroHit == SteamHeroHit.ShelfItem && shelfIndex >= 0 && shelfIndex < games.Count)
            return games[shelfIndex].AppId;

        if (heroHit is SteamHeroHit.HeroCard or SteamHeroHit.HeroAction)
        {
            SteamGameItem? hero = ResolveHero(games);
            return hero?.AppId;
        }

        return null;
    }

    public void Invalidate()
    {
        _shelfScrollY = 0f;
        _selectedHeroAppId = 0;
        _memo.Invalidate();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _renderer.Dispose();
    }

    private SteamGameItem? ResolveHero(IReadOnlyList<SteamGameItem> games)
    {
        if (games.Count == 0)
            return null;

        SteamGameItem? hero = null;
        if (_selectedHeroAppId != 0)
        {
            for (int i = 0; i < games.Count; i++)
            {
                if (games[i].AppId == _selectedHeroAppId)
                {
                    hero = games[i];
                    break;
                }
            }
        }

        hero ??= games[0];
        if (_selectedHeroAppId != hero.AppId)
            _selectedHeroAppId = hero.AppId;

        return hero;
    }

    private static void PopulateVisibleIds(
        SteamHeroGeometry geom,
        SteamGameItem? hero,
        IReadOnlyList<SteamGameItem> games,
        List<uint> destination)
    {
        if (hero is not null)
            destination.Add(hero.AppId);

        for (int i = 0; i < geom.ShelfItems.Length; i++)
        {
            SteamShelfItemGeometry item = geom.ShelfItems[i];
            if (item.Bounds.Bottom >= geom.ShelfBounds.Top
                && item.Bounds.Top <= geom.ShelfBounds.Bottom
                && item.Index >= 0
                && item.Index < games.Count)
            {
                uint appId = games[item.Index].AppId;
                if (hero is null || appId != hero.AppId)
                    destination.Add(appId);
            }
        }
    }
}
