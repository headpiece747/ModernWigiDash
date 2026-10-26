using System.Runtime.InteropServices;
using ModernWigiDash.Sdk;
using ModernWigiDash.Widgets.Steam.Models;
using ModernWigiDash.Widgets.Steam.Services;

namespace ModernWigiDash.Widgets.Steam.Rendering;

/// <summary>
/// The render-view memo for the Steam library: recomputes the grid/showcase
/// layout records only when their inputs change (bounds, scroll offset, game
/// count, flank visibility), and the filtered game list only when the
/// published snapshot reference or the Show-Uninstalled flag changes, so a
/// steady-state frame reuses both and the render tick allocates no per-frame
/// arrays. Render and touch read the same cached records, so the drawn cards
/// and the tap targets cannot drift apart. The widget holds only the records.
/// It also owns the showcase cursor (the focal card's selection and snap —
/// the clamp rule spelled once, so the selection and the snap cannot drift
/// apart).
/// </summary>
internal sealed class SteamGeometryMemo
{
    /// <summary>The scroll-offset epsilon under which the grid geometry is unchanged (a sub-pixel scroll is a steady state).</summary>
    private const float ScrollEpsilon = 1e-3f;

    // The three render-view memos (the shared memo slot owns the key-compare):
    // the grid geometry (the tolerance key: a sub-pixel scroll is a steady
    // state), the showcase geometry (the value key), and the filtered game
    // list (the reference key: a fresh snapshot record is a content change).
    private readonly MemoSlot<GridKey, SteamGridGeometry> _gridMemo = new(GridKeyComparer.Instance);
    private readonly MemoSlot<ShowcaseKey, SteamShowcaseGeometry> _showcaseMemo = new();
    private readonly MemoSlot<FilteredGamesKey, IReadOnlyList<SteamGameItem>> _filteredMemo = new(FilteredGamesKeyComparer.Instance);

    /// <summary>The showcase cursor (the focal card's index into the view's filtered list).</summary>
    private int _showcaseIndex;

    /// <summary>The grid memo key: bounds + scroll offset + game count.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct GridKey(SKRect Bounds, float ScrollX, int Count);

    /// <summary>The grid key's equality: a scroll offset within <see cref="ScrollEpsilon"/> is a steady state.</summary>
    private sealed class GridKeyComparer : IEqualityComparer<GridKey>
    {
        public static readonly GridKeyComparer Instance = new();

        public bool Equals(GridKey x, GridKey y)
            => x.Count == y.Count && x.Bounds == y.Bounds && Math.Abs(x.ScrollX - y.ScrollX) < ScrollEpsilon;

        public int GetHashCode(GridKey key) => HashCode.Combine(key.Count, key.Bounds, key.ScrollX);
    }

    /// <summary>The showcase memo key: bounds + flank visibility.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ShowcaseKey(SKRect Bounds, bool HasPrevious, bool HasNext);

    /// <summary>The filtered-list memo key: the snapshot reference + the Show-Uninstalled flag.</summary>
    private readonly record struct FilteredGamesKey(SteamLibrarySnapshot Snapshot, bool ShowUninstalled);

    /// <summary>The filtered-list key's equality: the snapshot compares by REFERENCE (the service publishes a fresh record each tick), the flag by value.</summary>
    private sealed class FilteredGamesKeyComparer : IEqualityComparer<FilteredGamesKey>
    {
        public static readonly FilteredGamesKeyComparer Instance = new();

        public bool Equals(FilteredGamesKey x, FilteredGamesKey y)
            => x.ShowUninstalled == y.ShowUninstalled && ReferenceEquals(x.Snapshot, y.Snapshot);

        public int GetHashCode(FilteredGamesKey key)
            => HashCode.Combine(key.ShowUninstalled, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Snapshot));
    }

    /// <summary>
    /// The grid geometry, recomputed only when the bounds, the scroll offset,
    /// or the game count changes. A zero-width bounds reads as the fallback
    /// design size (the not-yet-laid-out state).
    /// </summary>
    public SteamGridGeometry Grid(SKRect bounds, float scrollX, int gameCount, SKSize fallback)
    {
        SKRect rect = bounds.Width > 0f ? bounds : new SKRect(0, 0, fallback.Width, fallback.Height);
        var key = new GridKey(rect, scrollX, gameCount);
        if (_gridMemo.TryGet(key, out var value))
        {
            return value;
        }

        int columns = SteamGridLayout.ColumnsForWidth(rect.Width);
        value = SteamGridLayout.Compute(rect, columns, scrollX, gameCount);
        _gridMemo.Set(key, value);
        return value;
    }

    /// <summary>
    /// The showcase geometry, recomputed only when the bounds or the flank
    /// visibility changes (the grid memo's shape, for the showcase record).
    /// A zero-width bounds reads as the fallback design size.
    /// </summary>
    public SteamShowcaseGeometry Showcase(SKRect lastBounds, bool hasPrevious, bool hasNext, SKSize fallback)
    {
        SKRect rect = lastBounds.Width > 0f ? lastBounds : new SKRect(0, 0, fallback.Width, fallback.Height);
        var key = new ShowcaseKey(rect, hasPrevious, hasNext);
        if (_showcaseMemo.TryGet(key, out var value))
        {
            return value;
        }

        value = SteamShowcaseLayout.Compute(rect, hasPrevious, hasNext);
        _showcaseMemo.Set(key, value);
        return value;
    }

    // The filtered game list, keyed on the published snapshot reference (the
    // service publishes a fresh record each tick, so a reference change is a
    // content change) and the Show-Uninstalled flag.

    /// <summary>
    /// The render view's filtered game list: the snapshot's games as-is when
    /// Show-Uninstalled holds, otherwise the installed-only list (recomputed
    /// only on a snapshot-reference or flag change, so a steady-state tick
    /// allocates nothing).
    /// </summary>
    public IReadOnlyList<SteamGameItem> FilteredGames(SteamLibrarySnapshot snapshot, bool showUninstalled)
    {
        var key = new FilteredGamesKey(snapshot, showUninstalled);
        if (_filteredMemo.TryGet(key, out var value))
        {
            return value;
        }

        IReadOnlyList<SteamGameItem> list;
        if (showUninstalled)
        {
            list = snapshot.Games;
        }
        else
        {
            List<SteamGameItem> installed = [];
            foreach (SteamGameItem game in snapshot.Games)
            {
                if (game.State != SteamGameState.Uninstalled)
                    installed.Add(game);
            }

            list = installed;
        }

        _filteredMemo.Set(key, list);
        return list;
    }

    /// <summary>
    /// The showcase selection for the view: the cursor clamped to the view's
    /// range (the one owner of the clamp rule), the flanking cards, and the
    /// focal card; an empty view selects nothing.
    /// </summary>
    public (SteamGameItem? Focus, SteamGameItem? Previous, SteamGameItem? Next) Selection(IReadOnlyList<SteamGameItem> games)
    {
        if (games.Count == 0)
            return (null, null, null);
        _showcaseIndex = Math.Clamp(_showcaseIndex, 0, games.Count - 1);
        SteamGameItem? previous = _showcaseIndex > 0 ? games[_showcaseIndex - 1] : null;
        SteamGameItem? next = _showcaseIndex < games.Count - 1 ? games[_showcaseIndex + 1] : null;
        return (games[_showcaseIndex], previous, next);
    }

    /// <summary>Snaps the cursor one step within the view's range (the same clamp owner as <see cref="Selection"/>).</summary>
    public void Snap(IReadOnlyList<SteamGameItem> games, int direction)
        => _showcaseIndex = Math.Clamp(_showcaseIndex + direction, 0, Math.Max(0, games.Count - 1));

    /// <summary>Invalidates the memos (a rehydrated instance re-binds the
    /// service, and the cached records are stale against the new snapshot).</summary>
    public void Invalidate()
    {
        _gridMemo.Invalidate();
        _showcaseMemo.Invalidate();
        _filteredMemo.Invalidate();
    }
}
