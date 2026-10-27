using System.Runtime.InteropServices;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// Layout strategy for the Showcase mode: an editorial 3D-feeling carousel
/// with a focal game centered and flanking cards on either side.
/// </summary>
internal sealed class ShowcaseStrategy : ISteamLayoutStrategy
{
    private readonly SteamShowcaseRenderer _renderer = new();
    private readonly SteamGeometryMemo _memo;
    private readonly List<uint> _visibleIdsBuffer = [];
    private bool _disposed;

    public ShowcaseStrategy(SteamGeometryMemo memo)
    {
        _memo = memo;
    }

    public SteamLayoutMode Mode => SteamLayoutMode.Showcase;

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
        (SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next) = _memo.Selection(games);
        SteamShowcaseGeometry geom = _memo.Showcase(bounds, previous is not null, next is not null, fallback);

        _visibleIdsBuffer.Clear();
        PopulateVisibleIds(focus, previous, next, _visibleIdsBuffer);
        service.RequestPosters(games, CollectionsMarshal.AsSpan(_visibleIdsBuffer));

        _renderer.Render(
            canvas,
            bounds,
            geom,
            focus,
            previous,
            next,
            frame.Posters,
            snapshot.Persona,
            frame.Avatar,
            filterMode,
            games.Count,
            accent,
            text);
    }

    public void GetVisibleAppIds(
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback,
        List<uint> destination)
    {
        (SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next) = _memo.Selection(games);
        PopulateVisibleIds(focus, previous, next, destination);
    }

    public SteamInteractionResult HandleTap(
        SKPoint point,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback)
    {
        if (games.Count == 0)
            return SteamInteractionResult.None;

        (SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next) = _memo.Selection(games);
        SteamShowcaseGeometry geom = _memo.Showcase(bounds, previous is not null, next is not null, fallback);
        SteamShowcaseHit hit = SteamShowcaseLayout.HitTest(geom, point);

        switch (hit)
        {
            case SteamShowcaseHit.Previous:
                _memo.Snap(games, -1);
                return SteamInteractionResult.Snap(-1);

            case SteamShowcaseHit.Next:
                _memo.Snap(games, 1);
                return SteamInteractionResult.Snap(1);

            case SteamShowcaseHit.Focus:
                return focus is not null ? SteamInteractionResult.Activate(focus) : SteamInteractionResult.None;

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
        int direction = deltaX > 0f ? -1 : 1;
        _memo.Snap(games, direction);
    }

    public uint? FindTargetAppId(
        SKPoint point,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback)
    {
        if (games.Count == 0)
            return null;

        (SteamGameItem? focus, SteamGameItem? previous, SteamGameItem? next) = _memo.Selection(games);
        SteamShowcaseGeometry geom = _memo.Showcase(bounds, previous is not null, next is not null, fallback);
        SteamShowcaseHit hit = SteamShowcaseLayout.HitTest(geom, point);

        if (hit == SteamShowcaseHit.Focus && focus is not null)
            return focus.AppId;

        return null;
    }

    public void Invalidate()
    {
        _memo.Invalidate();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _renderer.Dispose();
    }

    private static void PopulateVisibleIds(
        SteamGameItem? focus,
        SteamGameItem? previous,
        SteamGameItem? next,
        List<uint> destination)
    {
        if (focus is not null)
            destination.Add(focus.AppId);
        if (previous is not null)
            destination.Add(previous.AppId);
        if (next is not null)
            destination.Add(next.AppId);
    }
}
