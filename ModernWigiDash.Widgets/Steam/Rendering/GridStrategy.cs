using System.Runtime.InteropServices;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// Layout strategy for the Poster Grid mode: a horizontally scrolling tactical
/// wall of 2:3 vertical game poster cards.
/// </summary>
internal sealed class GridStrategy : ISteamLayoutStrategy
{
    private readonly SteamGridRenderer _renderer = new();
    private readonly SteamGeometryMemo _memo;
    private readonly List<uint> _visibleIdsBuffer = [];
    private float _scrollX;
    private bool _disposed;

    public GridStrategy(SteamGeometryMemo memo)
    {
        _memo = memo;
    }

    public SteamLayoutMode Mode => SteamLayoutMode.Grid;

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
        SteamGridGeometry geom = _memo.Grid(bounds, _scrollX, games.Count, fallback);

        _visibleIdsBuffer.Clear();
        PopulateVisibleIds(geom, games, _visibleIdsBuffer);
        service.RequestPosters(games, CollectionsMarshal.AsSpan(_visibleIdsBuffer));

        _renderer.Render(canvas, bounds, games, geom, frame.Posters, filterMode, games.Count, accent, text);
    }

    public void GetVisibleAppIds(
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback,
        List<uint> destination)
    {
        SteamGridGeometry geom = _memo.Grid(bounds, _scrollX, games.Count, fallback);
        PopulateVisibleIds(geom, games, destination);
    }

    public SteamInteractionResult HandleTap(
        SKPoint point,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback)
    {
        if (games.Count == 0)
            return SteamInteractionResult.None;

        SteamGridGeometry geom = _memo.Grid(bounds, _scrollX, games.Count, fallback);
        int index = SteamGridLayout.HitTest(geom, point);

        if (index >= 0 && index < games.Count)
            return SteamInteractionResult.Activate(games[index]);

        return SteamInteractionResult.None;
    }

    public void HandleDrag(
        float deltaX,
        float deltaY,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback)
    {
        SteamGridGeometry geom = _memo.Grid(bounds, _scrollX, games.Count, fallback);
        _scrollX = Math.Clamp(_scrollX - deltaX, 0f, geom.MaxScroll);
    }

    public uint? FindTargetAppId(
        SKPoint point,
        SKRect bounds,
        IReadOnlyList<SteamGameItem> games,
        SKSize fallback)
    {
        if (games.Count == 0)
            return null;

        SteamGridGeometry geom = _memo.Grid(bounds, _scrollX, games.Count, fallback);
        int index = SteamGridLayout.HitTest(geom, point);

        if (index >= 0 && index < games.Count)
            return games[index].AppId;

        return null;
    }

    public void Invalidate()
    {
        _scrollX = 0f;
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
        SteamGridGeometry geom,
        IReadOnlyList<SteamGameItem> games,
        List<uint> destination)
    {
        for (int i = 0; i < geom.CellCount; i++)
        {
            int index = geom.Indexes[i];
            if (index >= 0 && index < games.Count)
                destination.Add(games[index].AppId);
        }
    }
}
